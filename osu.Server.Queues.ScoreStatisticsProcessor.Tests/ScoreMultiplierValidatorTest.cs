// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Online.API;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Osu.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class ScoreMultiplierValidatorTest : DatabaseTest
    {
        private readonly Beatmap beatmap;

        public ScoreMultiplierValidatorTest()
        {
            beatmap = AddBeatmap(b =>
            {
                b.diff_size = 3;
                b.diff_approach = 4;
                b.diff_drain = 5;
                b.diff_overall = 6;
            });
            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id, attr => attr.Mods = [new OsuModDoubleTime()]);
        }

        [Fact]
        public void TestScoreWhereTotalScoreWithoutModsIsMissingIsRejected()
        {
            using var conn = Processor.GetDatabaseConnection();
            var score = CreateTestScore(0, beatmap.beatmap_id);
            score.Score.ScoreData.TotalScoreWithoutMods = null;
            InsertScore(conn, score);

            Processor.PushToQueue(score);
            WaitForDatabaseState("SELECT `ranked` FROM `scores` WHERE `id` = @id", 0, CancellationToken, new { score.Score.id });
            WaitForDatabaseState("SELECT `total_score` FROM `osu_user_stats` WHERE user_id = 2", 0, CancellationToken);
        }

        [Fact]
        public void TestScoreWhereTotalScoreWithoutModsMismatchesTotalScoreIsAdjustedAutomatically()
        {
            using var conn = Processor.GetDatabaseConnection();
            var score = CreateTestScore(0, beatmap.beatmap_id);
            score.Score.ScoreData.TotalScoreWithoutMods = 1_000_000;
            score.Score.ScoreData.Mods = [new APIMod(new OsuModDoubleTime())];
            score.Score.total_score = 1_100_000;
            InsertScore(conn, score);

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT `total_score` FROM `scores` WHERE `id` = @id", 1_230_000, CancellationToken, new { score.Score.id });
            // classic score = round(100814.25 * standardised score / 1000000)
            WaitForDatabaseState("SELECT `total_score` FROM `osu_user_stats` WHERE user_id = 2", 124_002, CancellationToken);
        }

        [Fact]
        public void TestScoreWhereTotalScoreWithoutModsIsCorrectIsUntouched()
        {
            using var conn = Processor.GetDatabaseConnection();
            var score = CreateTestScore(0, beatmap.beatmap_id);
            score.Score.ScoreData.TotalScoreWithoutMods = 1_000_000;
            score.Score.ScoreData.Mods = [new APIMod(new OsuModDoubleTime())];
            score.Score.total_score = 1_230_000;
            InsertScore(conn, score);

            PushToQueueAndWaitForProcess(score);
            WaitForDatabaseState("SELECT `total_score` FROM `scores` WHERE `id` = @id", 1_230_000, CancellationToken, new { score.Score.id });
            // classic score = round(100814.25 * standardised score / 1000000)
            WaitForDatabaseState("SELECT `total_score` FROM `osu_user_stats` WHERE user_id = 2", 124_002, CancellationToken);
        }

        [Fact]
        public void TestModsWithSettings()
        {
            using var conn = Processor.GetDatabaseConnection();
            var score = CreateTestScore(0, beatmap.beatmap_id);
            score.Score.ScoreData.TotalScoreWithoutMods = 1_000_000;
            score.Score.ScoreData.Mods = [new APIMod(new OsuModNightcore { SpeedChange = { Value = 2 } }), new APIMod(new OsuModHidden { OnlyFadeApproachCircles = { Value = true } })];
            score.Score.total_score = 1_200_000;
            InsertScore(conn, score);

            Processor.PushToQueue(score);
            WaitForDatabaseState("SELECT `total_score` FROM `scores` WHERE `id` = @id", 1_479_000, CancellationToken, new { score.Score.id });
            // classic score = round(100814.25 * standardised score / 1000000)
            WaitForDatabaseState("SELECT `total_score` FROM `osu_user_stats` WHERE user_id = 2", 149_104, CancellationToken);
        }

        [Fact]
        public void TestDifficultyAdjust()
        {
            using var conn = Processor.GetDatabaseConnection();
            var score = CreateTestScore(0, beatmap.beatmap_id);
            score.Score.ScoreData.TotalScoreWithoutMods = 1_000_000;
            score.Score.ScoreData.Mods =
            [
                new APIMod(new OsuModDifficultyAdjust
                {
                    ApproachRate = { Value = 4.2f },
                    CircleSize = { Value = 3.5f },
                })
            ];
            score.Score.total_score = 500_000;
            InsertScore(conn, score);

            Processor.PushToQueue(score);
            WaitForDatabaseState("SELECT `total_score` FROM `scores` WHERE `id` = @id", 675_000, CancellationToken, new { score.Score.id });
            // classic score = round(100814.25 * standardised score / 1000000)
            WaitForDatabaseState("SELECT `total_score` FROM `osu_user_stats` WHERE user_id = 2", 68050, CancellationToken);
        }
    }
}
