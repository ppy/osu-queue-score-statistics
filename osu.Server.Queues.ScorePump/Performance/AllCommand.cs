// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScorePump.Performance
{
    [Command(Name = "all", Description = "Computes pp of all users.")]
    public class AllCommand : PerformanceCommand
    {
        private const int max_users_per_query = 10000;

        [Option(Description = "Continue where a previously aborted 'all' run left off.")]
        public bool Continue { get; set; }

        private int? totalCount;
        private int processedCount;

        public async Task<int> OnExecuteAsync(CommandLineApplication app)
        {
            LegacyDatabaseHelper.RulesetDatabaseInfo databaseInfo = LegacyDatabaseHelper.GetRulesetSpecifics(RulesetId);

            long currentUserId;

            using (var db = Queue.GetDatabaseConnection())
            {
                if (Continue)
                    currentUserId = await GetCount(db, databaseInfo.LastProcessedPpUserCount);
                else
                {
                    currentUserId = 0;
                    await SetCount(db, databaseInfo.LastProcessedPpUserCount, 0);
                }

                totalCount = await db.QuerySingleAsync<int?>($"SELECT COUNT(`user_id`) FROM {databaseInfo.UserStatsTable} WHERE `user_id` >= @UserId", new
                {
                    UserId = currentUserId
                });

                if (totalCount == null)
                    throw new InvalidOperationException("Could not find user ID count.");
            }

            Console.WriteLine($"Processing all users with ID larger than {currentUserId}");
            Console.WriteLine($"Processed 0 of {totalCount}");

            while (true)
            {
                uint[] users;

                using (var db = Queue.GetDatabaseConnection())
                {
                    users = (await db.QueryAsync<uint>($"SELECT `user_id` FROM {databaseInfo.UserStatsTable} WHERE `user_id` > @UserId ORDER BY `user_id` LIMIT @Limit", new
                    {
                        UserId = currentUserId,
                        Limit = max_users_per_query
                    })).ToArray();
                }

                if (users.Length == 0)
                    break;

                await processUsers(users);

                currentUserId = Math.Max(currentUserId, users.Max());

                using (var db = Queue.GetDatabaseConnection())
                    await SetCount(db, databaseInfo.LastProcessedPpUserCount, currentUserId);
            }

            return 0;
        }

        private async Task processUsers(IEnumerable<uint> userIds)
        {
            await Task.WhenAll(Partitioner
                               .Create(userIds)
                               .GetPartitions(Threads)
                               .AsParallel()
                               .Select(processPartition));

            async Task processPartition(IEnumerator<uint> partition)
            {
                using (partition)
                {
                    while (partition.MoveNext())
                    {
                        await Task.Yield();

                        await processUser(partition.Current);

                        Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {totalCount}");
                    }
                }
            }
        }

        private async Task processUser(uint userId)
        {
            SoloScore[] scores;

            using (var db = Queue.GetDatabaseConnection())
            {
                scores = (await db.QueryAsync<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE `user_id` = @UserId", new
                {
                    UserId = userId
                })).ToArray();
            }

            foreach (SoloScore score in scores)
            {
                Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);
                Mod[] mods = score.ScoreInfo.mods.Select(m => m.ToMod(ruleset)).ToArray();
                ScoreInfo scoreInfo = score.ScoreInfo.ToScoreInfo(mods);

                DifficultyAttributes? difficultyAttributes = await GetDifficultyAttributes(score, ruleset, mods);
                if (difficultyAttributes == null)
                    continue;

                PerformanceAttributes? performanceAttributes = ruleset.CreatePerformanceCalculator()?.Calculate(scoreInfo, difficultyAttributes);
                if (performanceAttributes == null)
                    continue;

                using (var db = Queue.GetDatabaseConnection())
                {
                    await db.ExecuteAsync($"INSERT INTO {SoloScorePerformance.TABLE_NAME} (`score_id`, `pp`) VALUES (@ScoreId, @Pp) ON DUPLICATE KEY UPDATE `pp` = @Pp", new
                    {
                        ScoreId = score.id,
                        Pp = performanceAttributes.Total
                    });
                }
            }
        }
    }
}
