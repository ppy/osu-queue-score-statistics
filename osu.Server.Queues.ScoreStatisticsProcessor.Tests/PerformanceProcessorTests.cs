// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Localisation;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Catch.Mods;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Difficulty;
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
            AddBeatmapAttributes<OsuDifficultyAttributes>();

            SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.build_id = TestBuildID;
                score.Score.preserve = true;
            });

            WaitForDatabaseState("SELECT COUNT(*) FROM osu_user_stats WHERE rank_score > 0 AND user_id = 2", 1, CancellationToken);
            WaitForDatabaseState("SELECT rank_score_index FROM osu_user_stats WHERE user_id = 2", 1, CancellationToken);
        }

        [Fact]
        public void PerformanceDoesNotDoubleAfterScoreSetOnSameMap()
        {
            AddBeatmap();
            AddBeatmapAttributes<OsuDifficultyAttributes>(setup: attr =>
            {
                attr.AimDifficulty = 3;
                attr.SpeedDifficulty = 3;
                attr.OverallDifficulty = 3;
            });

            SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.build_id = TestBuildID;
                score.Score.preserve = true;
            });

            // 165pp from the single score above + 2pp from playcount bonus
            WaitForDatabaseState("SELECT rank_score FROM osu_user_stats WHERE user_id = 2", 167, CancellationToken);

            // purposefully identical to score above, to confirm that you don't get pp for two scores on the same map twice
            SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.build_id = TestBuildID;
                score.Score.preserve = true;
            });

            // 165pp from the single score above + 4pp from playcount bonus
            WaitForDatabaseState("SELECT rank_score FROM osu_user_stats WHERE user_id = 2", 169, CancellationToken);
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
                Assert.True(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScoreInfo(), new[] { mod }), mod.GetType().ReadableName());
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
                Assert.False(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScoreInfo(), new[] { mod }), mod.GetType().ReadableName());
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
                Assert.False(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScoreInfo(), new[] { mod }), mod.GetType().ReadableName());
        }

        [Fact]
        public void ModsThatGivePpAreAllowed()
        {
            // Not an extensive list.
            var mods = new Mod[]
            {
                // Osu
                new OsuModMuted(),
                new OsuModDaycore(),
                // Taiko
                new TaikoModMuted(),
                new TaikoModDaycore(),
                // Catch
                new CatchModMuted(),
                new CatchModDaycore(),
                // Mania
                new ManiaModMuted(),
                new ManiaModDaycore(),
            };

            foreach (var mod in mods)
                Assert.True(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScoreInfo(), new[] { mod }), mod.GetType().ReadableName());
        }

        [Fact]
        public void ClassicAllowedOnLegacyScores()
        {
            Assert.True(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScoreInfo { LegacyScoreId = 1 }, new Mod[] { new OsuModClassic() }));
        }

        [Fact]
        public void ClassicDisallowedOnNonLegacyScores()
        {
            Assert.False(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScoreInfo(), new Mod[] { new OsuModClassic() }));
        }

        [Fact]
        public void ModsWithSettingsAreDisallowed()
        {
            var mods = new Mod[]
            {
                new OsuModDoubleTime { SpeedChange = { Value = 1.1 } },
                new OsuModClassic { NoSliderHeadAccuracy = { Value = false } },
                new OsuModFlashlight { SizeMultiplier = { Value = 2 } }
            };

            foreach (var mod in mods)
                Assert.False(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScoreInfo(), new[] { mod }), mod.GetType().ReadableName());
        }

        [Fact]
        public void FailedScoreDoesNotProcess()
        {
            AddBeatmap();
            AddBeatmapAttributes<OsuDifficultyAttributes>();

            ScoreItem score = SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.passed = false;
            });

            WaitForDatabaseState("SELECT COUNT(*) FROM scores WHERE id = @ScoreId AND pp IS NOT NULL", 0, CancellationToken, new
            {
                ScoreId = score.Score.id
            });
        }

        [Fact]
        public void LegacyScoreDoesProcess()
        {
            AddBeatmap();
            AddBeatmapAttributes<OsuDifficultyAttributes>();

            ScoreItem score = SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.legacy_score_id = 1;
                score.Score.preserve = true;
            });

            WaitForDatabaseState("SELECT COUNT(*) FROM scores WHERE id = @ScoreId AND pp IS NOT NULL AND ranked = 1 AND preserve = 1", 1, CancellationToken, new
            {
                ScoreId = score.Score.id
            });
        }

        [Fact]
        public void NonLegacyScoreWithNoBuildIdIsNotRanked()
        {
            AddBeatmap();
            AddBeatmapAttributes<OsuDifficultyAttributes>();

            ScoreItem score = SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.preserve = true;
            });

            WaitForDatabaseState("SELECT COUNT(*) FROM scores WHERE id = @ScoreId AND pp IS NULL", 1, CancellationToken, new
            {
                ScoreId = score.Score.id
            });
        }

        [Fact]
        public void ScoresThatHavePpButInvalidModsGetsNoPP()
        {
            AddBeatmap();
            AddBeatmapAttributes<OsuDifficultyAttributes>();

            ScoreItem score;

            using (MySqlConnection conn = Processor.GetDatabaseConnection())
            {
                score = CreateTestScore(beatmapId: TEST_BEATMAP_ID);

                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.build_id = TestBuildID;
                score.Score.ScoreData.Mods = new[] { new APIMod(new InvalidMod()) };
                score.Score.preserve = true;

                conn.Insert(score.Score);

                PushToQueueAndWaitForProcess(score);
            }

            WaitForDatabaseState("SELECT COUNT(*) FROM scores WHERE id = @ScoreId AND pp IS NULL", 1, CancellationToken, new
            {
                ScoreId = score.Score.id
            });
        }

        private class InvalidMod : Mod
        {
            public override string Name => "Invalid";
            public override LocalisableString Description => "Invalid";
            public override double ScoreMultiplier => 1;
            public override string Acronym => "INVALID";
        }
    }
}
