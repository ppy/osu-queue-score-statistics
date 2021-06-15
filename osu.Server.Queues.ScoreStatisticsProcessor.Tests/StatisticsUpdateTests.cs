using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using osu.Game.Rulesets.Scoring;
using Xunit;
using Xunit.Sdk;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class StatisticsUpdateTests : IDisposable
    {
        private readonly ScoreStatisticsProcessor processor;

        private readonly CancellationTokenSource cts = new CancellationTokenSource(10000);

        public StatisticsUpdateTests()
        {
            processor = new ScoreStatisticsProcessor();
            processor.ClearQueue();

            using (var db = processor.GetDatabaseConnection())
            {
                // just a safety measure for now to ensure we don't hit production. since i was running on production until now.
                // will throw if not on test database.
                db.Query<int>("SELECT * FROM test_database");

                db.Execute("TRUNCATE TABLE osu_user_stats");
                db.Execute("TRUNCATE TABLE osu_user_stats_mania");
                db.Execute("TRUNCATE TABLE solo_scores");
                db.Execute("TRUNCATE TABLE solo_scores_process_history");
            }

            Task.Run(() => processor.Run(cts.Token), cts.Token);
        }

        [Fact]
        public void TestPlaycountIncreaseMania()
        {
            waitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(CreateTestScore(3));
            waitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", 1, cts.Token);

            processor.PushToQueue(CreateTestScore(3));
            waitForDatabaseState("SELECT playcount FROM osu_user_stats_mania WHERE user_id = 2", 2, cts.Token);
        }

        [Fact]
        public void TestPlaycountIncrease()
        {
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, cts.Token);

            processor.PushToQueue(CreateTestScore());
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 2, cts.Token);
        }

        [Fact]
        public void TestPlaycountReprocessSameScoreDoesntIncrease()
        {
            var score = CreateTestScore();

            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", (int?)null, cts.Token);

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, cts.Token);

            // the score will be marked as processed (in the database) at this point, so should not increase the playcount if processed a second time.
            score.MarkProcessed();

            processor.PushToQueue(score);
            waitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, cts.Token);
        }

        private static long scoreIDSource;

        public static ScoreItem CreateTestScore(int rulesetId = 0)
        {
            return new ScoreItem
            {
                Score = new SoloScore
                {
                    user_id = 2,
                    beatmap_id = 81,
                    ruleset_id = rulesetId,
                    statistics =
                    {
                        { HitResult.Perfect, 300 }
                    },
                    id = Interlocked.Increment(ref scoreIDSource),
                    passed = true
                }
            };
        }

        private void waitForDatabaseState<T>(string sql, T expected, CancellationToken cancellationToken)
        {
            T lastValue = default;

            using (var db = processor.GetDatabaseConnection())
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    lastValue = db.QueryFirstOrDefault<T>(sql);
                    if (lastValue.Equals(expected))
                        return;

                    Thread.Sleep(50);
                }
            }

            throw new XunitException($"Database criteria was not met ({sql}: expected {expected} != actual {lastValue})");
        }

#pragma warning disable CA1816
        public void Dispose()
#pragma warning restore CA1816
        {
            cts.Cancel();
        }
    }
}
