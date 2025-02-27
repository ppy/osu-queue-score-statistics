// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Localisation;
using osu.Game.Online.API;
using osu.Game.Rulesets.Catch.Mods;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Taiko.Difficulty;
using osu.Game.Rulesets.Taiko.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class PerformanceProcessorTests : DatabaseTest
    {
        public PerformanceProcessorTests()
        {
            using (var db = Processor.GetDatabaseConnection())
            {
                db.Execute("TRUNCATE TABLE osu_scores_high");
                db.Execute("TRUNCATE TABLE osu_beatmap_difficulty_attribs");
            }
        }

        [Fact]
        public void PerformanceIndexUpdates()
        {
            AddBeatmap();

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
            });

            SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.build_id = TestBuildID;
                score.Score.preserve = true;
            });

            // 158pp from the single score above + 2pp from playcount bonus
            WaitForDatabaseState("SELECT rank_score FROM osu_user_stats WHERE user_id = 2", 160, CancellationToken);

            // purposefully identical to score above, to confirm that you don't get pp for two scores on the same map twice
            SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.build_id = TestBuildID;
                score.Score.preserve = true;
            });

            // 158pp from the single score above + 4pp from playcount bonus
            WaitForDatabaseState("SELECT rank_score FROM osu_user_stats WHERE user_id = 2", 162, CancellationToken);
        }

        /// <summary>
        /// Dear reader:
        /// This test will likely appear very random to you. However it in fact exercises a very particular code path.
        /// Both client- and server-side components do some rather gnarly juggling to convert between the various score-shaped models
        /// (`SoloScore`, `ScoreInfo`, `SoloScoreInfo`...)
        /// For most difficulty calculators this doesn't matter because they access fairly simple properties.
        /// However taiko pp calculation code has _convert detection_ inside.
        /// Therefore it is _very_ important that the single particular access path that the taiko pp calculator uses right now
        /// (https://github.com/ppy/osu/blob/555305bf7f650a3461df1e23832ff99b94ca710e/osu.Game.Rulesets.Taiko/Difficulty/TaikoPerformanceCalculator.cs#L44-L45)
        /// has the ID of the ruleset for the beatmap BEFORE CONVERSION.
        /// This attempts to exercise that requirement in a bit of a dodgy way so that nobody silently breaks taiko pp on accident.
        /// </summary>
        [Fact]
        public void TestTaikoNonConvertsCalculatePPCorrectly()
        {
            AddBeatmap(b => b.playmode = 1);
            AddBeatmapAttributes<TaikoDifficultyAttributes>(setup: attr =>
            {
                attr.StaminaDifficulty = 3;
                attr.RhythmDifficulty = 3;
                attr.ColourDifficulty = 3;
            }, mode: 1);

            SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ruleset_id = 1;
                score.Score.ScoreData.Mods = [new APIMod(new TaikoModHidden())];
                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.build_id = TestBuildID;
                score.Score.preserve = true;
            });

            // 218 from the single score above + 2pp from playcount bonus
            WaitForDatabaseState("SELECT rank_score FROM osu_user_stats_taiko WHERE user_id = 2", 220, CancellationToken);
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
                Assert.True(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScore(), new[] { mod }), mod.GetType().ReadableName());
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
                Assert.False(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScore(), new[] { mod }), mod.GetType().ReadableName());
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
                Assert.False(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScore(), new[] { mod }), mod.GetType().ReadableName());
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
                Assert.True(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScore(), new[] { mod }), mod.GetType().ReadableName());
        }

        [Fact]
        public void ClassicAllowedOnLegacyScores()
        {
            Assert.True(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScore { legacy_score_id = 1 }, new Mod[] { new OsuModClassic() }));
        }

        [Fact]
        public void ClassicDisallowedOnNonLegacyScores()
        {
            Assert.False(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScore(), new Mod[] { new OsuModClassic() }));
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
                Assert.False(ScorePerformanceProcessor.AllModsValidForPerformance(new SoloScore(), new[] { mod }), mod.GetType().ReadableName());
        }

        [Fact]
        public void FailedScoreDoesNotProcess()
        {
            AddBeatmap();

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

        [Fact(Skip = "ScorePerformanceProcessor is disabled for legacy scores for now: https://github.com/ppy/osu-queue-score-statistics/pull/212#issuecomment-2011297448.")]
        public void LegacyScoreIsProcessedAndPpIsWrittenBackToLegacyTables()
        {
            AddBeatmap();

            using (MySqlConnection conn = Processor.GetDatabaseConnection())
                conn.Execute("INSERT INTO osu_scores_high (score_id, user_id) VALUES (1, 0)");

            ScoreItem score = SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.legacy_score_id = 1;
                score.Score.preserve = true;
            });

            WaitForDatabaseState("SELECT COUNT(*) FROM scores WHERE id = @ScoreId AND pp IS NULL AND ranked = 1 AND preserve = 1", 1, CancellationToken, new
            {
                ScoreId = score.Score.id
            });

            WaitForDatabaseState("SELECT COUNT(*) FROM osu_scores_high WHERE score_id = 1 AND pp IS NOT NULL", 1, CancellationToken, new
            {
                ScoreId = score.Score.id
            });
        }

        [Fact]
        public void NonLegacyScoreWithNoBuildIdIsNotRanked()
        {
            AddBeatmap();

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

        [Theory]
        [InlineData(null, 799)]
        [InlineData(0, 799)]
        [InlineData(850, 799)]
        [InlineData(150, 150)]
        public async Task UserHighestRankUpdates(int? highestRankBefore, int highestRankAfter)
        {
            // simulate fake users to beat as we climb up ranks.
            // this is going to be a bit of a chonker query...
            using var db = Processor.GetDatabaseConnection();

            var stringBuilder = new StringBuilder();

            stringBuilder.Append("INSERT INTO osu_user_stats (`user_id`, `rank_score`, `rank_score_index`, "
                                 + "`accuracy_total`, `accuracy_count`, `accuracy`, `accuracy_new`, `playcount`, `ranked_score`, `total_score`, "
                                 + "`x_rank_count`, `xh_rank_count`, `s_rank_count`, `sh_rank_count`, `a_rank_count`, `rank`, `level`) VALUES ");

            for (int i = 0; i < 1000; ++i)
            {
                if (i > 0)
                    stringBuilder.Append(',');

                stringBuilder.Append($"({1000 + i}, {1000 - i}, {i}, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1)");
            }

            await db.ExecuteAsync(stringBuilder.ToString());

            if (highestRankBefore != null)
            {
                await db.ExecuteAsync("INSERT INTO `osu_user_performance_rank_highest` (`user_id`, `mode`, `rank`) VALUES (@userId, @mode, @rank)", new
                {
                    userId = 2,
                    mode = 0,
                    rank = highestRankBefore.Value,
                });
            }

            var beatmap = AddBeatmap();

            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.preserve = s.Score.ranked = true;
                s.Score.pp = 200; // ~202 pp total, including bonus pp
            });

            WaitForDatabaseState("SELECT `rank` FROM `osu_user_performance_rank_highest` WHERE `user_id` = @userId AND `mode` = @mode", highestRankAfter, CancellationToken, new
            {
                userId = 2,
                mode = 0,
            });
        }

        [Fact]
        public async Task RankIndexPartitionCaching()
        {
            // simulate fake users to beat as we climb up ranks.
            // this is going to be a bit of a chonker query...
            using var db = Processor.GetDatabaseConnection();

            var stringBuilder = new StringBuilder();

            stringBuilder.Append("INSERT INTO osu_user_stats (`user_id`, `rank_score`, `rank_score_index`, "
                                 + "`accuracy_total`, `accuracy_count`, `accuracy`, `accuracy_new`, `playcount`, `ranked_score`, `total_score`, "
                                 + "`x_rank_count`, `xh_rank_count`, `s_rank_count`, `sh_rank_count`, `a_rank_count`, `rank`, `level`) VALUES ");

            // Each fake user is spaced 25 pp apart.
            // This knowledge can be used to deduce expected values of following assertions.
            for (int i = 0; i < 1000; ++i)
            {
                if (i > 0)
                    stringBuilder.Append(',');

                stringBuilder.Append($"({1000 + i}, {25 * (1000 - i)}, {i + 1}, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1)");
            }

            await db.ExecuteAsync(stringBuilder.ToString());

            var firstBeatmap = AddBeatmap(b => b.beatmap_id = 1);

            SetScoreForBeatmap(firstBeatmap.beatmap_id, s =>
            {
                s.Score.preserve = s.Score.ranked = true;
                s.Score.pp = 150; // ~152 pp total, including bonus pp
            });

            WaitForDatabaseState("SELECT `rank_score_index` FROM `osu_user_stats` WHERE `user_id` = @userId", 995, CancellationToken, new
            {
                userId = 2,
            });

            SetScoreForBeatmap(firstBeatmap.beatmap_id, s =>
            {
                s.Score.preserve = s.Score.ranked = true;
                s.Score.pp = 180; // ~184 pp total, including bonus pp
            });

            WaitForDatabaseState("SELECT `rank_score_index` FROM `osu_user_stats` WHERE `user_id` = @userId", 994, CancellationToken, new
            {
                userId = 2,
            });

            var secondBeatmap = AddBeatmap(b => b.beatmap_id = 2);

            SetScoreForBeatmap(secondBeatmap.beatmap_id, s =>
            {
                s.Score.preserve = s.Score.ranked = true;
                s.Score.pp = 300; // ~486 pp total, including bonus pp
            });

            WaitForDatabaseState("SELECT `rank_score_index` FROM `osu_user_stats` WHERE `user_id` = @userId", 982, CancellationToken, new
            {
                userId = 2,
            });
        }

        [Fact]
        public async Task UserDailyRankUpdates()
        {
            // simulate fake users to beat as we climb up ranks.
            // this is going to be a bit of a chonker query...
            using var db = Processor.GetDatabaseConnection();

            var stringBuilder = new StringBuilder();

            stringBuilder.Append("INSERT INTO osu_user_stats (`user_id`, `rank_score`, `rank_score_index`, "
                                 + "`accuracy_total`, `accuracy_count`, `accuracy`, `accuracy_new`, `playcount`, `ranked_score`, `total_score`, "
                                 + "`x_rank_count`, `xh_rank_count`, `s_rank_count`, `sh_rank_count`, `a_rank_count`, `rank`, `level`) VALUES ");

            for (int i = 0; i < 1000; ++i)
            {
                if (i > 0)
                    stringBuilder.Append(',');

                stringBuilder.Append($"({1000 + i}, {1000 - i}, {i}, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1)");
            }

            await db.ExecuteAsync(stringBuilder.ToString());
            await db.ExecuteAsync("REPLACE INTO `osu_counts` (name, count) VALUES ('pp_rank_column_osu', 13)");

            var beatmap = AddBeatmap();

            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.preserve = s.Score.ranked = true;
                s.Score.pp = 200; // ~202 pp total, including bonus pp
            });

            WaitForDatabaseState("SELECT `r13` FROM `osu_user_performance_rank` WHERE `user_id` = @userId AND `mode` = @mode", 799, CancellationToken, new
            {
                userId = 2,
                mode = 0,
            });

            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.preserve = s.Score.ranked = true;
                s.Score.pp = 400; // ~404 pp total, including bonus pp
            });

            WaitForDatabaseState("SELECT `r13` FROM `osu_user_performance_rank` WHERE `user_id` = @userId AND `mode` = @mode", 597, CancellationToken, new
            {
                userId = 2,
                mode = 0,
            });

            await db.ExecuteAsync("REPLACE INTO `osu_counts` (name, count) VALUES ('pp_rank_column_osu', 14)");

            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.preserve = s.Score.ranked = true;
                s.Score.pp = 600; // ~606 pp total, including bonus pp
            });

            WaitForDatabaseState("SELECT `r13`, `r14` FROM `osu_user_performance_rank` WHERE `user_id` = @userId AND `mode` = @mode", (597, 395), CancellationToken, new
            {
                userId = 2,
                mode = 0,
            });
        }

        [Fact]
        public async Task MissingAttributesThrowsError()
        {
            var beatmap = AddBeatmap();

            // Delete attributes - this could happen either as a result of diffcalc not being run or being out of date and not inserting some required attributes.
            using (var db = Processor.GetDatabaseConnection())
                await db.ExecuteAsync("TRUNCATE TABLE osu_beatmap_difficulty_attribs");

            using (var db = Processor.GetDatabaseConnection())
            {
                var beatmapStore = await BeatmapStore.CreateAsync(db);

                await Assert.ThrowsAnyAsync<DifficultyAttributesMissingException>(() => beatmapStore.GetDifficultyAttributesAsync(beatmap, new OsuRuleset(), [], db));
                await Assert.ThrowsAnyAsync<DifficultyAttributesMissingException>(() => beatmapStore.GetDifficultyAttributesAsync(beatmap, new OsuRuleset(), [], db));
                await Assert.ThrowsAnyAsync<DifficultyAttributesMissingException>(() => beatmapStore.GetDifficultyAttributesAsync(beatmap, new OsuRuleset(), [], db));
            }

            Assert.ThrowsAny<Exception>(() => SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.ScoreData.Statistics[HitResult.Great] = 100;
                score.Score.max_combo = 100;
                score.Score.accuracy = 1;
                score.Score.build_id = TestBuildID;
                score.Score.preserve = true;
            }));
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
