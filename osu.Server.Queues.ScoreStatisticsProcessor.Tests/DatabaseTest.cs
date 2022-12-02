// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Dapper.Contrib.Extensions;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;
using Xunit.Sdk;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    [Collection("Database tests")] // Ensure all tests hitting the database are run sequentially (no parallel execution).
    public abstract class DatabaseTest : IDisposable
    {
        protected readonly ScoreStatisticsProcessor Processor;

        protected CancellationToken CancellationToken => cancellationSource.Token;

        protected const int MAX_COMBO = 1337;

        protected const int TEST_BEATMAP_ID = 1;
        protected const int TEST_BEATMAP_SET_ID = 1;

        private readonly CancellationTokenSource cancellationSource = new CancellationTokenSource(10000);

        protected DatabaseTest()
        {
            Processor = new ScoreStatisticsProcessor();
            Processor.ClearQueue();

            using (var db = Processor.GetDatabaseConnection())
            {
                // just a safety measure for now to ensure we don't hit production. since i was running on production until now.
                // will throw if not on test database.
                if (db.QueryFirstOrDefault<int?>("SELECT * FROM osu_counts WHERE name = 'is_production'") != null)
                    throw new InvalidOperationException("You are trying to do something very silly.");

                db.Execute("TRUNCATE TABLE osu_user_stats");
                db.Execute("TRUNCATE TABLE osu_user_stats_mania");
                db.Execute("TRUNCATE TABLE osu_user_beatmap_playcount");
                db.Execute("TRUNCATE TABLE osu_user_month_playcount");
                db.Execute($"TRUNCATE TABLE {Beatmap.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {BeatmapSet.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {SoloScore.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {ProcessHistory.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {SoloScorePerformance.TABLE_NAME}");

                AddBeatmap();
            }

            Task.Run(() => Processor.Run(CancellationToken), CancellationToken);
        }

        private static ulong scoreIDSource;

        public static ScoreItem CreateTestScore(int rulesetId = 0)
        {
            var row = new SoloScore
            {
                id = Interlocked.Increment(ref scoreIDSource),
                user_id = 2,
                beatmap_id = TEST_BEATMAP_ID,
                ruleset_id = rulesetId,
                created_at = DateTimeOffset.Now,
                updated_at = DateTimeOffset.Now,
            };

            var startTime = new DateTimeOffset(new DateTime(2020, 02, 05));
            var scoreInfo = new SoloScoreInfo
            {
                ID = row.id,
                UserID = row.user_id,
                BeatmapID = row.beatmap_id,
                RulesetID = row.ruleset_id,
                StartedAt = startTime,
                EndedAt = startTime + TimeSpan.FromSeconds(180),
                MaxCombo = MAX_COMBO,
                TotalScore = 100000,
                Rank = ScoreRank.S,
                Statistics =
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 0 }
                },
                Passed = true
            };

            row.ScoreInfo = scoreInfo;

            return new ScoreItem(row);
        }

        protected void AddBeatmap(Action<Beatmap, BeatmapSet>? setup = null)
        {
            var beatmap = new Beatmap
            {
                beatmap_id = TEST_BEATMAP_ID,
                beatmapset_id = TEST_BEATMAP_SET_ID,
                approved = BeatmapOnlineStatus.Ranked,
            };

            var beatmapSet = new BeatmapSet
            {
                beatmapset_id = TEST_BEATMAP_SET_ID,
                approved = BeatmapOnlineStatus.Ranked,
            };

            setup?.Invoke(beatmap, beatmapSet);

            using (var db = Processor.GetDatabaseConnection())
            {
                try
                {
                    db.Insert(beatmap);
                    db.Insert(beatmapSet);
                }
                catch
                {
                    db.Update(beatmap);
                    db.Update(beatmapSet);
                }
            }
        }

        protected void WaitForTotalProcessed(int count, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Processor.TotalProcessed == count)
                    return;

                Thread.Sleep(50);
            }

            throw new XunitException("All scores were not successfully processed");
        }

        protected void WaitForDatabaseState<T>(string sql, T expected, CancellationToken cancellationToken)
        {
            using (var db = Processor.GetDatabaseConnection())
            {
                while (true)
                {
                    if (!Debugger.IsAttached)
                        cancellationToken.ThrowIfCancellationRequested();

                    var lastValue = db.QueryFirstOrDefault<T>(sql);
                    if ((expected == null && lastValue == null) || expected?.Equals(lastValue) == true)
                        return;

                    Thread.Sleep(50);
                }
            }
        }

#pragma warning disable CA1816
        public void Dispose()
#pragma warning restore CA1816
        {
            cancellationSource.Cancel();
        }
    }
}
