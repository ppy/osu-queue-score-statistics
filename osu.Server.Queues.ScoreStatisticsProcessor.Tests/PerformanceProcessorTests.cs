// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using osu.Framework.Extensions.TypeExtensions;
using osu.Game.Rulesets.Catch.Mods;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Taiko.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class PerformanceProcessorTests : DatabaseTest
    {
        public PerformanceProcessorTests()
        {
            using (var db = Processor.GetDatabaseConnection())
            {
                db.Execute("TRUNCATE TABLE osu_beatmap_difficulty_attribs");
            }
        }

        [Fact]
        public void PerformanceIndexUpdates()
        {
            AddBeatmap();
            AddBeatmapAttributes();

            SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreInfo.Statistics[HitResult.Great] = 100;
                score.Score.ScoreInfo.MaxCombo = 100;
                score.Score.ScoreInfo.Accuracy = 1;
                score.Score.preserve = true;
            });

            WaitForDatabaseState("SELECT COUNT(*) FROM osu_user_stats WHERE rank_score_exp > 0 AND user_id = 2", 1, CancellationToken);
            WaitForDatabaseState("SELECT rank_score_index_exp FROM osu_user_stats WHERE user_id = 2", 1, CancellationToken);
        }

        [Fact]
        public void LegacyModsThatGivePpAreAllowed()
        {
            var mods = new Mod[]
            {
                // Osu
                new OsuModEasy(),
                new OsuModNoFail(),
                new OsuModHalfTime(),
                new OsuModHardRock(),
                new OsuModSuddenDeath(),
                new OsuModPerfect(),
                new OsuModDoubleTime(),
                new OsuModNightcore(),
                new OsuModHidden(),
                new OsuModFlashlight(),
                new OsuModSpunOut(),
                // Taiko
                new TaikoModEasy(),
                new TaikoModNoFail(),
                new TaikoModHalfTime(),
                new TaikoModHardRock(),
                new TaikoModSuddenDeath(),
                new TaikoModPerfect(),
                new TaikoModDoubleTime(),
                new TaikoModNightcore(),
                new TaikoModHidden(),
                new TaikoModFlashlight(),
                // Catch
                new CatchModEasy(),
                new CatchModNoFail(),
                new CatchModHalfTime(),
                new CatchModHardRock(),
                new CatchModSuddenDeath(),
                new CatchModPerfect(),
                new CatchModDoubleTime(),
                new CatchModNightcore(),
                new CatchModHidden(),
                new CatchModFlashlight(),
                // Mania
                new ManiaModEasy(),
                new ManiaModNoFail(),
                new ManiaModHalfTime(),
                new ManiaModSuddenDeath(),
                new ManiaModKey4(),
                new ManiaModKey5(),
                new ManiaModKey6(),
                new ManiaModKey7(),
                new ManiaModKey8(),
                new ManiaModKey9(),
                new ManiaModMirror(),
            };

            foreach (var mod in mods)
                Assert.True(ScorePerformanceProcessor.AllModsValidForPerformance(new[] { mod }), mod.GetType().ReadableName());
        }

        [Fact]
        public void LegacyModsThatDoNotGivePpAreDisallowed()
        {
            var mods = new Mod[]
            {
                // Osu
                new OsuModRelax(),
                new OsuModAutopilot(),
                new OsuModTargetPractice(),
                new OsuModAutoplay(),
                new OsuModCinema(),
                // Taiko
                new TaikoModRelax(),
                new TaikoModAutoplay(),
                // Catch
                new CatchModRelax(),
                new CatchModAutoplay(),
                // Mania
                new ManiaModHardRock(),
                new ManiaModKey1(),
                new ManiaModKey2(),
                new ManiaModKey3(),
                new ManiaModKey10(),
                new ManiaModDualStages(),
                new ManiaModRandom(),
                new ManiaModAutoplay(),
            };

            foreach (var mod in mods)
                Assert.False(ScorePerformanceProcessor.AllModsValidForPerformance(new[] { mod }), mod.GetType().ReadableName());
        }

        [Fact]
        public void ModsThatDoNotGivePpAreDisallowed()
        {
            // Not an extensive list.
            var mods = new Mod[]
            {
                new ModWindUp(),
                new ModWindDown(),
                // Osu
                new OsuModDeflate(),
                new OsuModApproachDifferent(),
                new OsuModDifficultyAdjust(),
                // Taiko
                new TaikoModRandom(),
                new TaikoModSwap(),
                // Catch
                new CatchModMirror(),
                new CatchModFloatingFruits(),
                new CatchModDifficultyAdjust(),
                // Mania
                new ManiaModInvert(),
                new ManiaModConstantSpeed(),
            };

            foreach (var mod in mods)
                Assert.False(ScorePerformanceProcessor.AllModsValidForPerformance(new[] { mod }), mod.GetType().ReadableName());
        }

        [Fact]
        public void ModsThatGivePpAreAllowed()
        {
            // Not an extensive list.
            var mods = new Mod[]
            {
                // Osu
                new OsuModMuted(),
                new OsuModClassic(),
                new OsuModDaycore(),
                // Taiko
                new TaikoModMuted(),
                new TaikoModClassic(),
                new TaikoModDaycore(),
                // Catch
                new CatchModMuted(),
                new CatchModClassic(),
                new CatchModDaycore(),
                // Mania
                new ManiaModMuted(),
                new ManiaModClassic(),
                new ManiaModDaycore(),
            };

            foreach (var mod in mods)
                Assert.True(ScorePerformanceProcessor.AllModsValidForPerformance(new[] { mod }), mod.GetType().ReadableName());
        }

        [Fact]
        public void FailedScoreDoesNotProcess()
        {
            AddBeatmap();
            AddBeatmapAttributes();

            ScoreItem score = SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreInfo.Statistics[HitResult.Great] = 100;
                score.Score.ScoreInfo.MaxCombo = 100;
                score.Score.ScoreInfo.Accuracy = 1;
                score.Score.ScoreInfo.Passed = false;
            });

            WaitForDatabaseState("SELECT COUNT(*) FROM solo_scores_process_history WHERE score_id = @ScoreId", 1, CancellationToken, new
            {
                ScoreId = score.Score.id
            });

            WaitForDatabaseState("SELECT COUNT(*) FROM solo_scores_performance WHERE score_id = @ScoreId", 0, CancellationToken, new
            {
                ScoreId = score.Score.id
            });
        }
    }
}
