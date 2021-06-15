using Dapper;
using Dapper.Contrib.Extensions;
using DeepEqual.Syntax;
using osu.Game.Rulesets.Scoring;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class SerialisationTests
    {
        private readonly ScoreStatisticsProcessor processor;

        public SerialisationTests()
        {
            processor = new ScoreStatisticsProcessor();

            using (var db = processor.GetDatabaseConnection())
            {
                // just a safety measure for now to ensure we don't hit production. since i was running on production until now.
                // will throw if not on test database.
                db.Query<int>("SELECT * FROM test_database");

                db.Execute("TRUNCATE TABLE solo_scores");
            }
        }

        [Fact]
        public void TestSoloScoreSerialisation()
        {
            var score = new SoloScore
            {
                user_id = 2,
                beatmap_id = 81,
                ruleset_id = 3,
                id = 1,
                statistics =
                {
                    { HitResult.Perfect, 300 }
                },
                passed = true,
            };

            using (var db = processor.GetDatabaseConnection())
            {
                db.Insert(score);
                var retrieved = db.QueryFirst<SoloScore>("SELECT * FROM solo_scores");
                score.ShouldDeepEqual(retrieved);
            }
        }
    }
}
