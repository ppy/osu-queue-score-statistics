using System.Diagnostics;
using Dapper;
using Dapper.Contrib.Extensions;
using DeepEqual.Syntax;
using osu.Game.IO.Serialization;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
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

                db.Execute($"TRUNCATE TABLE {SoloScore.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {ProcessHistory.TABLE_NAME}");
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

                db.QueryFirst<ProcessHistory>($"SELECT * FROM {ProcessHistory.TABLE_NAME}").ShouldDeepEqual(score.ProcessHistory);
            }
        }

        [Fact]
        public void TestSoloScoreDirectSerialisation()
        {
            var score = StatisticsUpdateTests.CreateTestScore().Score;

            var serialised = score.Serialize();
            var deserialised = serialised.Deserialize<SoloScore>();

            Debug.Assert(deserialised != null);

            // ignore time values for now until we can figure how to test without precision issues.
            deserialised.created_at = score.created_at;
            deserialised.updated_at = score.updated_at;

            deserialised.ShouldDeepEqual(score);
        }

        [Fact]
        public void TestSoloScoreDatabaseSerialisation()
        {
            using (var db = processor.GetDatabaseConnection())
            {
                var score = StatisticsUpdateTests.CreateTestScore().Score;

                db.Insert(score);

                var retrieved = db.QueryFirst<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME}");

                // ignore time values for now until we can figure how to test without precision issues.
                retrieved.created_at = score.created_at;
                retrieved.updated_at = score.updated_at;

                retrieved.ShouldDeepEqual(score);
            }
        }
    }
}
