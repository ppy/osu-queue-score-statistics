using Dapper;
using Dapper.Contrib.Extensions;
using DeepEqual.Syntax;
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
                db.Execute("TRUNCATE TABLE solo_scores_process_history");
            }
        }

        [Fact]
        public void TestProcessHistorySerialisation()
        {
            using (var db = processor.GetDatabaseConnection())
            {
                var score = StatisticsUpdateTests.CreateTestScore();

                score.MarkProcessed();

                db.Insert(score.ProcessHistory);

                db.QueryFirst<ProcessHistory>("SELECT * FROM solo_scores_process_history").ShouldDeepEqual(score.ProcessHistory);
            }
        }

        [Fact]
        public void TestSoloScoreSerialisation()
        {
            using (var db = processor.GetDatabaseConnection())
            {
                var score = StatisticsUpdateTests.CreateTestScore().Score;

                db.Insert(score);
                db.QueryFirst<SoloScore>("SELECT * FROM solo_scores").ShouldDeepEqual(score);
            }
        }
    }
}
