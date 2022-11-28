// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit.Sdk;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public abstract class StatisticsProcessorTest : IDisposable
    {
        protected readonly ScoreStatisticsProcessor Processor;

        protected readonly CancellationTokenSource Cts = new CancellationTokenSource(10000);

        protected const int MAX_COMBO = 1337;

        protected const int TEST_BEATMAP_ID = 172;

        protected const int TEST_BEATMAP_SET_ID = 76;

        protected StatisticsProcessorTest()
        {
            Processor = new ScoreStatisticsProcessor();
            Processor.ClearQueue();

            using (var db = Processor.GetDatabaseConnection())
            {
                // just a safety measure for now to ensure we don't hit production. since i was running on production until now.
                // will throw if not on test database.
                db.Query<int>("SELECT * FROM test_database");

                db.Execute("TRUNCATE TABLE osu_user_stats");
                db.Execute("TRUNCATE TABLE osu_user_stats_mania");
                db.Execute("TRUNCATE TABLE osu_user_beatmap_playcount");
                db.Execute("TRUNCATE TABLE osu_user_month_playcount");
                db.Execute($"TRUNCATE TABLE {SoloScore.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {ProcessHistory.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {SoloScorePerformance.TABLE_NAME}");
            }

            Task.Run(() => Processor.Run(Cts.Token), Cts.Token);
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
                Beatmap = new APIBeatmap
                {
                    OnlineID = TEST_BEATMAP_ID,
                    BeatmapSet = new APIBeatmapSet
                    {
                        OnlineID = TEST_BEATMAP_SET_ID,
                    }
                },
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
            Cts.Cancel();
        }
    }
}
