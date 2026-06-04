// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Dapper;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Scoring.Legacy;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;

#pragma warning disable CS0618 // Type or member is obsolete

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class ScoreMultiplierRecalculationTest : DatabaseTest
    {
        public ScoreMultiplierRecalculationTest()
        {
            using (var db = Processor.GetDatabaseConnection())
            {
                // table is not in osu-web migrations, so create manually for now.
                db.Execute("DROP TABLE IF EXISTS osu_beatmap_scoring_attribs");
                db.Execute("""
                           create table osu_beatmap_scoring_attribs
                           (
                               beatmap_id               mediumint unsigned not null,
                               mode                     tinyint unsigned   not null,
                               legacy_accuracy_score    int    default 0   not null,
                               legacy_combo_score       bigint default 0   not null,
                               legacy_bonus_score_ratio float  default 0   not null,
                               legacy_bonus_score       int    default 0   not null,
                               max_combo                int    default 0   not null,
                               primary key (beatmap_id, mode)
                           );
                           """);
            }
        }

        /// <summary>
        /// A score coming <b>from stable</b>, <b>without</b> <see cref="SoloScoreData.TotalScoreWithoutMods"/> populated:
        /// <list type="bullet">
        /// <item>
        /// Does <b>not</b> need <see cref="SoloScoreData.TotalScoreWithoutMods"/> to be populated by <see cref="PopulateTotalScoreWithoutModsCommand"/>,
        /// because its total score in the new table is entirely derived from <see cref="SoloScore.legacy_total_score"/>
        /// via <see cref="StandardisedScoreMigrationTools.UpdateFromLegacy(ScoreInfo,Ruleset,LegacyBeatmapConversionDifficultyInfo,LegacyScoreAttributes)"/>.
        /// </item>
        /// <item>
        /// <b>Will</b> have its total score recalculated successfully in <see cref="RecalculateModMultipliersCommand"/> via the aforementioned method.
        /// </item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task TestStableScoreWithoutPopulatedTotalScoreWithoutMods()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 83338;
                b.total_length = 118;
                b.hit_length = 114;
                b.countTotal = 800;
                b.countNormal = 453;
                b.countSlider = 172;
                b.countSpinner = 1;
                b.diff_drain = 7;
                b.diff_size = 4;
                b.diff_overall = 8;
                b.diff_approach = 8;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 5.28005f;
            });

            await conn.ExecuteAsync(
                """
                INSERT INTO `osu_beatmap_scoring_attribs`
                    (`beatmap_id`, `mode`, `legacy_accuracy_score`, `legacy_combo_score`, `legacy_bonus_score_ratio`, `legacy_bonus_score`, `max_combo`)
                VALUES
                    (83338, 0, 201420, 17653380, 0.0516779, 14900, 916)
                """);

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/3901512042
                id = 3901512042,
                user_id = 14715160,
                ruleset_id = 0,
                beatmap_id = beatmap.beatmap_id,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.S,
                passed = true,
                accuracy = 0.962726,
                max_combo = 915,
                total_score = 1001683,
                ScoreData = new SoloScoreData
                {
                    Mods = [new APIMod(new OsuModDoubleTime()), new APIMod(new OsuModHardRock()), new APIMod(new OsuModClassic())],
                    Statistics =
                    {
                        [HitResult.Ok] = 35,
                        [HitResult.Great] = 591,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Great] = 626,
                        [HitResult.LegacyComboIncrease] = 290,
                    },
                },
                pp = 742.889,
                legacy_score_id = 4724388079,
                legacy_total_score = 20414445,
                started_at = null,
                ended_at = new DateTimeOffset(2024, 11, 22, 17, 10, 59, TimeSpan.Zero),
                build_id = null,
            };

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", (long?)null, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            // `StandardisedScoreMigrationTools.UpdateFromLegacy()` calculates `TotalScoreWithoutMods` as 894871
            // 894871 * 1.23 (DT) * 1.09 (HR) * 0.985 (CL) = 1181757.2464545 ≈ 1181757
            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 1181757, CancellationToken, score);

            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 1181757, CancellationToken, score);
        }

        /// <summary>
        /// A score coming <b>from stable</b>, <b>with</b> <see cref="SoloScoreData.TotalScoreWithoutMods"/> populated:
        /// <list type="bullet">
        /// <item>
        /// Does <b>not</b> need <see cref="PopulateTotalScoreWithoutModsCommand"/> to do anything.
        /// </item>
        /// <item>
        /// <b>Will</b> have its total score recalculated successfully in <see cref="RecalculateModMultipliersCommand"/>
        /// from <see cref="SoloScore.legacy_total_score"/>
        /// via <see cref="StandardisedScoreMigrationTools.UpdateFromLegacy(ScoreInfo,Ruleset,LegacyBeatmapConversionDifficultyInfo,LegacyScoreAttributes)"/>.
        /// In this context the stored value of <see cref="SoloScoreData.TotalScoreWithoutMods"/> is <b>redundant</b> and may be dropped in the future to reduce storage footprint.
        /// </item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task TestStableScoreWithPopulatedTotalScoreWithoutMods()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 1872396;
                b.total_length = 284;
                b.hit_length = 273;
                b.countTotal = 1670;
                b.countNormal = 843;
                b.countSlider = 412;
                b.countSpinner = 1;
                b.diff_drain = 5.5f;
                b.diff_size = 3.8f;
                b.diff_overall = 9.2f;
                b.diff_approach = 9.2f;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 5.94851f;
            });
            await conn.ExecuteAsync(
                """
                INSERT INTO `osu_beatmap_scoring_attribs`
                    (`beatmap_id`, `mode`, `legacy_accuracy_score`, `legacy_combo_score`, `legacy_bonus_score_ratio`, `legacy_bonus_score`, `max_combo`)
                VALUES
                    (1872396, 0, 401940, 64680900, 0.0468354, 39500, 1682)
                """);

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/6619896601
                id = 6619896601,
                user_id = 18604347,
                ruleset_id = 0,
                beatmap_id = beatmap.beatmap_id,
                has_replay = false,
                preserve = true,
                ranked = true,
                rank = ScoreRank.A,
                passed = true,
                accuracy = 0.916003,
                max_combo = 257,
                total_score = 239050,
                ScoreData = new SoloScoreData
                {
                    Mods = [new APIMod(new OsuModDoubleTime()), new APIMod(new OsuModEasy()), new APIMod(new OsuModClassic())],
                    Statistics =
                    {
                        [HitResult.Ok] = 87,
                        [HitResult.Meh] = 27,
                        [HitResult.Miss] = 25,
                        [HitResult.Great] = 1117,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Great] = 1256,
                        [HitResult.LegacyComboIncrease] = 426,
                    },
                    TotalScoreWithoutMods = 452747,
                },
                pp = 169.214,
                legacy_score_id = 5018534802,
                legacy_total_score = 3491637,
                started_at = null,
                ended_at = new DateTimeOffset(2026, 5, 1, 1, 9, 7, TimeSpan.Zero),
                build_id = null,
            };

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", 452747, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            // 452747 * 1.23 (DT) * 0.8 (EZ) * 0.985 (CL) = 438820.50228 ≈ 438821
            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 438821, CancellationToken, score);

            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 438821, CancellationToken, score);
        }

        /// <summary>
        /// Mostly redundant with the above two tests, just added for extra coverage of the stable "no-mod" case
        /// (which in the new tables ends up having mods anyway because of the tacking-on of Classic mod).
        /// </summary>
        [Fact]
        public async Task TestStableScoreWithClassicModOnly()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 41082;
                b.total_length = 157;
                b.hit_length = 113;
                b.countTotal = 267;
                b.countNormal = 112;
                b.countSlider = 64;
                b.countSpinner = 9;
                b.diff_drain = 3;
                b.diff_size = 3;
                b.diff_overall = 3;
                b.diff_approach = 3;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 2.64658f;
            });
            await conn.ExecuteAsync(
                """
                INSERT INTO `osu_beatmap_scoring_attribs`
                    (`beatmap_id`, `mode`, `legacy_accuracy_score`, `legacy_combo_score`, `legacy_bonus_score_ratio`, `legacy_bonus_score`, `max_combo`)
                VALUES
                    (41082, 0, 60090, 925452, 0.0501326, 113100, 274)
                """);

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/6508885
                id = 6508885,
                user_id = 284905,
                ruleset_id = 0,
                beatmap_id = beatmap.beatmap_id,
                has_replay = false,
                preserve = true,
                ranked = true,
                rank = ScoreRank.B,
                passed = true,
                accuracy = 0.89009,
                max_combo = 52,
                total_score = 417278,
                ScoreData = new SoloScoreData
                {
                    Mods = [new APIMod(new OsuModClassic())],
                    Statistics =
                    {
                        [HitResult.Ok] = 16,
                        [HitResult.Meh] = 2,
                        [HitResult.Miss] = 8,
                        [HitResult.Great] = 159,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Great] = 185,
                        [HitResult.LegacyComboIncrease] = 89,
                    },
                },
                pp = 6.27829f,
                legacy_score_id = 71242252,
                legacy_total_score = 179568,
                started_at = null,
                ended_at = new DateTimeOffset(2010, 3, 12, 1, 52, 13, TimeSpan.Zero),
                build_id = null,
            };

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", (long?)null, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            // `StandardisedScoreMigrationTools.UpdateFromLegacy()` calculates `TotalScoreWithoutMods` as 434665
            // 434665 * 0.985 (CL) = 428145.025 ≈ 428145
            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 428145, CancellationToken, score);
        }

        /// <summary>
        /// A score coming <b>from lazer</b>, with <b>no mods present</b>, that <b>does not</b> have <see cref="SoloScoreData.TotalScoreWithoutMods"/> populated:
        /// <list type="bullet">
        /// <item>
        /// Does <b>not</b> need <see cref="PopulateTotalScoreWithoutModsCommand"/> to do anything,
        /// because it would only duplicate <see cref="SoloScore.total_score"/> into <see cref="SoloScoreData.TotalScoreWithoutMods"/>.
        /// It is assumed to be a core invariant that a score without mods always has 1.0x score multiplier.
        /// </item>
        /// <item>
        /// Does <b>not</b> need to be processed by <see cref="RecalculateModMultipliersCommand"/> at all.
        /// </item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task TestLazerScoreWithoutModsAndWithoutTotalScoreWithoutModsPopulated()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 3890663;
                b.total_length = 133;
                b.hit_length = 132;
                b.countTotal = 1726;
                b.countNormal = 1244;
                b.countSlider = 241;
                b.countSpinner = 0;
                b.diff_drain = 5;
                b.diff_size = 4;
                b.diff_overall = 9.5f;
                b.diff_approach = 9.8f;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 7.7486f;
            });

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/3177540316
                id = 3177540316,
                user_id = 8788058,
                ruleset_id = 0,
                beatmap_id = beatmap.beatmap_id,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.A,
                passed = true,
                accuracy = 0.983011,
                max_combo = 575,
                total_score = 705648,
                ScoreData = new SoloScoreData
                {
                    Mods = [],
                    Statistics =
                    {
                        [HitResult.Ok] = 19,
                        [HitResult.Meh] = 2,
                        [HitResult.Miss] = 11,
                        [HitResult.Great] = 1453,
                        [HitResult.IgnoreHit] = 239,
                        [HitResult.IgnoreMiss] = 6,
                        [HitResult.LargeTickHit] = 34,
                        [HitResult.SliderTailHit] = 237,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Great] = 1485,
                        [HitResult.IgnoreHit] = 241,
                        [HitResult.LargeTickHit] = 34,
                        [HitResult.SliderTailHit] = 241,
                    },
                },
                pp = 450.81f,
                legacy_score_id = null,
                legacy_total_score = 0,
                started_at = new DateTimeOffset(2024, 7, 18, 5, 15, 7, TimeSpan.Zero),
                ended_at = new DateTimeOffset(2024, 7, 18, 5, 17, 26, TimeSpan.Zero),
                build_id = 7596,
            };

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", (long?)null, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 705648, CancellationToken, score);

            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 705648, CancellationToken, score);
        }

        /// <summary>
        /// A score coming <b>from lazer</b>, with <b>any mods present</b>, that <b>does not</b> have <see cref="SoloScoreData.TotalScoreWithoutMods"/> populated:
        /// <list type="bullet">
        /// <item>
        /// <b>Will</b> have <see cref="SoloScoreData.TotalScoreWithoutMods"/> populated by <see cref="PopulateTotalScoreWithoutModsCommand"/>.
        /// This backpopulation <b>will</b> incur <b>one-time</b> precision loss due to total scores being rounded to full integers and floating-point error.
        /// </item>
        /// <item>
        /// <b>Will</b> be processed by <see cref="RecalculateModMultipliersCommand"/>, with the primary input being <see cref="SoloScoreData.TotalScoreWithoutMods"/>.
        /// </item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task TestLazerScoreWithModsAndWithoutTotalScoreWithoutModsPopulated()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 2834369;
                b.total_length = 309;
                b.hit_length = 298;
                b.countTotal = 2109;
                b.countNormal = 1568;
                b.countSlider = 266;
                b.countSpinner = 3;
                b.diff_drain = 4.5f;
                b.diff_size = 5.4f;
                b.diff_overall = 9;
                b.diff_approach = 8.5f;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 5.50778f;
            });

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/3176143420
                id = 3176143420,
                user_id = 5182050,
                ruleset_id = 0,
                beatmap_id = beatmap.beatmap_id,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.B,
                passed = true,
                accuracy = 0.859511,
                max_combo = 617,
                total_score = 200732,
                ScoreData = new SoloScoreData
                {
                    Mods = [new APIMod(new OsuModNoFail()), new APIMod(new OsuModDoubleTime())],
                    Statistics =
                    {
                        [HitResult.Ok] = 292,
                        [HitResult.Meh] = 53,
                        [HitResult.Miss] = 36,
                        [HitResult.Great] = 1456,
                        [HitResult.IgnoreHit] = 264,
                        [HitResult.IgnoreMiss] = 22,
                        [HitResult.LargeBonus] = 2,
                        [HitResult.SmallBonus] = 21,
                        [HitResult.LargeTickHit] = 189,
                        [HitResult.LargeTickMiss] = 1,
                        [HitResult.SliderTailHit] = 257,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Great] = 1837,
                        [HitResult.IgnoreHit] = 266,
                        [HitResult.LargeBonus] = 12,
                        [HitResult.SmallBonus] = 22,
                        [HitResult.LargeTickHit] = 190,
                        [HitResult.SliderTailHit] = 266,
                    },
                },
                pp = 171.482f,
                legacy_score_id = null,
                legacy_total_score = 0,
                started_at = new DateTimeOffset(2024, 7, 17, 21, 39, 37, TimeSpan.Zero),
                ended_at = new DateTimeOffset(2024, 7, 17, 21, 43, 5, TimeSpan.Zero),
                build_id = 7596,
            };

            await conn.ExecuteAsync(@"INSERT INTO `osu_builds` (`build_id`, `version`, `stream_id`) VALUES (7596, '2024.625.2-lazer-windows', NULL)");

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", 364967, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            // 364967 * 0.5 (NF) * 1.23 (DT) = 224454.705 ≈ 224455
            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 224455, CancellationToken, score);

            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 224455, CancellationToken, score);
        }

        /// <summary>
        /// A score coming <b>from lazer</b>, with <b>no mods present</b>, that <b>has</b> <see cref="SoloScoreData.TotalScoreWithoutMods"/> populated:
        /// <list type="bullet">
        /// <item>
        /// Does <b>not</b> need <see cref="PopulateTotalScoreWithoutModsCommand"/> to do anything, as the value is already there.
        /// Because it is assumed to be a core invariant that a score without mods always has 1.0x score multiplier,
        /// <see cref="SoloScoreData.TotalScoreWithoutMods"/> is <b>redundant</b> for these scores and may be dropped in the future to reduce storage footprint.
        /// </item>
        /// <item>
        /// Does <b>not</b> need to be processed by <see cref="RecalculateModMultipliersCommand"/> at all.
        /// </item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task TestLazerScoreWithoutModsAndWithTotalScoreWithoutModsPopulated()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 3816123;
                b.total_length = 169;
                b.hit_length = 164;
                b.countTotal = 968;
                b.countNormal = 379;
                b.countSlider = 293;
                b.countSpinner = 1;
                b.diff_drain = 5.8f;
                b.diff_size = 3;
                b.diff_overall = 8.4f;
                b.diff_approach = 8.8f;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 5.58702f;
            });

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/6619895904
                id = 6619895904,
                user_id = 19401270,
                ruleset_id = 0,
                beatmap_id = beatmap.beatmap_id,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.S,
                passed = true,
                accuracy = 0.969128f,
                max_combo = 1022,
                total_score = 908044,
                ScoreData = new SoloScoreData
                {
                    Mods = [],
                    Statistics =
                    {
                        [HitResult.Ok] = 29,
                        [HitResult.Meh] = 2,
                        [HitResult.Great] = 642,
                        [HitResult.IgnoreHit] = 293,
                        [HitResult.IgnoreMiss] = 13,
                        [HitResult.LargeBonus] = 8,
                        [HitResult.SmallBonus] = 15,
                        [HitResult.LargeTickHit] = 65,
                        [HitResult.SliderTailHit] = 284,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Great] = 673,
                        [HitResult.IgnoreHit] = 293,
                        [HitResult.LargeBonus] = 12,
                        [HitResult.SmallBonus] = 15,
                        [HitResult.LargeTickHit] = 65,
                        [HitResult.SliderTailHit] = 293,
                    },
                    TotalScoreWithoutMods = 908044,
                },
                pp = 194.284f,
                legacy_score_id = null,
                legacy_total_score = 0,
                started_at = new DateTimeOffset(2026, 5, 1, 1, 6, 0, TimeSpan.Zero),
                ended_at = new DateTimeOffset(2026, 5, 1, 1, 8, 54, TimeSpan.Zero),
                build_id = 8683,
            };

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", 908044, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 908044, CancellationToken, score);

            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 908044, CancellationToken, score);
        }

        /// <summary>
        /// A score coming <b>from lazer</b>, with <b>any mods present</b>, that <b>does not</b> have <see cref="SoloScoreData.TotalScoreWithoutMods"/> populated:
        /// <list type="bullet">
        /// <item>
        /// Does <b>not</b> need <see cref="PopulateTotalScoreWithoutModsCommand"/> to do anything, as the value is already there.
        /// </item>
        /// <item>
        /// <b>Will</b> be processed by <see cref="RecalculateModMultipliersCommand"/>, with the primary input being <see cref="SoloScoreData.TotalScoreWithoutMods"/>.
        /// </item>
        /// </list>
        /// </summary>
        [Fact]
        public async Task TestLazerScoreWithModsAndWithTotalScoreWithoutModsPopulated()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 5508772;
                b.total_length = 75;
                b.hit_length = 62;
                b.countTotal = 250;
                b.countNormal = 157;
                b.countSlider = 93;
                b.countSpinner = 0;
                b.diff_drain = 5.2f;
                b.diff_size = 3.6f;
                b.diff_overall = 9.2f;
                b.diff_approach = 9.3f;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 6.24303f;
            });

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/6619896551
                id = 6619896551,
                user_id = 31849423,
                ruleset_id = 0,
                beatmap_id = beatmap.beatmap_id,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.B,
                passed = true,
                accuracy = 0.853683f,
                max_combo = 93,
                total_score = 468585,
                ScoreData = new SoloScoreData
                {
                    Mods = [new APIMod(new OsuModHardRock()), new APIMod(new OsuModNightcore()), new APIMod(new OsuModHidden())],
                    Statistics =
                    {
                        [HitResult.Ok] = 41,
                        [HitResult.Meh] = 2,
                        [HitResult.Miss] = 13,
                        [HitResult.Great] = 194,
                        [HitResult.IgnoreHit] = 91,
                        [HitResult.IgnoreMiss] = 5,
                        [HitResult.LargeTickHit] = 8,
                        [HitResult.SliderTailHit] = 90,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Great] = 250,
                        [HitResult.IgnoreHit] = 93,
                        [HitResult.LargeTickHit] = 8,
                        [HitResult.SliderTailHit] = 93,
                    },
                    TotalScoreWithoutMods = 379126,
                },
                pp = 433.214f,
                legacy_score_id = null,
                legacy_total_score = 0,
                started_at = new DateTimeOffset(2026, 5, 1, 1, 8, 18, TimeSpan.Zero),
                ended_at = new DateTimeOffset(2026, 5, 1, 1, 9, 6, TimeSpan.Zero),
                build_id = 8686,
            };

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", 379126, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            // 379126 * 1.09 (HR) * 1.23 (NC) * 1.04 (HD) = 528625.997328 ≈ 528626
            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 528626, CancellationToken, score);

            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 528626, CancellationToken, score);
        }

        /// <summary>
        /// Mostly intended to cover correct calculation of the score multiplier when Difficulty Adjust is present.
        /// </summary>
        [Fact]
        public async Task TestLazerScoreWithDifficultyAdjust()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 2911355;
                b.total_length = 302;
                b.total_length = 301;
                b.countTotal = 2391;
                b.countNormal = 1687;
                b.countSlider = 389;
                b.countSpinner = 2;
                b.diff_drain = 5;
                b.diff_size = 4.3f;
                b.diff_overall = 9.5f;
                b.diff_approach = 9.7f;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 7.25036f;
            });

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/3175574960
                id = 3175574960,
                user_id = 6404583,
                ruleset_id = 0,
                beatmap_id = beatmap.beatmap_id,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.C,
                passed = true,
                accuracy = 0.746018,
                max_combo = 170,
                total_score = 84678,
                ScoreData = new SoloScoreData
                {
                    Mods =
                    [
                        new APIMod(new OsuModNightcore { SpeedChange = { Value = 1.49 } }),
                        new APIMod(new OsuModDifficultyAdjust
                        {
                            ApproachRate = { Value = 9 },
                            OverallDifficulty = { Value = 9 },
                        }),
                    ],
                    Statistics =
                    {
                        [HitResult.Ok] = 490,
                        [HitResult.Meh] = 56,
                        [HitResult.Miss] = 162,
                        [HitResult.Great] = 1290,
                        [HitResult.IgnoreHit] = 864,
                        [HitResult.IgnoreMiss] = 81,
                        [HitResult.LargeBonus] = 1,
                        [HitResult.SmallBonus] = 18,
                        [HitResult.LargeTickHit] = 186,
                        [HitResult.LargeTickMiss] = 10,
                        [HitResult.SliderTailHit] = 338,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Great] = 1998,
                        [HitResult.IgnoreHit] = 389,
                        [HitResult.LargeBonus] = 4,
                        [HitResult.SmallBonus] = 12,
                        [HitResult.LargeTickHit] = 196,
                        [HitResult.SliderTailHit] = 389,
                    },
                },
                pp = null,
                legacy_score_id = null,
                legacy_total_score = 0,
                started_at = new DateTimeOffset(2024, 7, 17, 19, 27, 33, TimeSpan.Zero),
                ended_at = new DateTimeOffset(2024, 7, 17, 19, 31, 0, TimeSpan.Zero),
                build_id = 7596,
            };

            await conn.ExecuteAsync(@"INSERT INTO `osu_builds` (`build_id`, `version`, `stream_id`) VALUES (7596, '2024.625.2-lazer-windows', NULL)");

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", 156811, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            // 156811 * 1.174 (NC 1.49x) * 0.65 (DA, AR portion) * 0.75 (DA, OD portion) = 89746.855575 ≈ 89747
            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 89747, CancellationToken, score);

            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 89747, CancellationToken, score);
        }

        /// <summary>
        /// An osu!mania score set:
        /// <list type="bullet">
        /// <item><b>in lazer</b>,</item>
        /// <item>on a <b>converted beatmap</b>,</item>
        /// <item>with <b>a key mod present</b>,</item>
        /// <item>that <b>does not</b> have <see cref="SoloScoreData.TotalScoreWithoutMods"/> populated,</item>
        /// <item>set on a date <b>preceding</b> the release of https://github.com/ppy/osu/pull/30506,</item>
        /// <item>set on a build <b>before</b> the release of https://github.com/ppy/osu/pull/30506,</item>
        /// </list>
        /// should receive the old multiplier of 1.0x when populating <see cref="SoloScoreData.TotalScoreWithoutMods"/>.
        /// </summary>
        [Fact]
        public async Task TestLazerManiaScoreWithOldKeyModMultiplierAndWithoutTotalScoreWithoutModsPopulated()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 714175;
                b.total_length = 80;
                b.hit_length = 77;
                b.countTotal = 453;
                b.countNormal = 213;
                b.countSlider = 120;
                b.countSpinner = 0;
                b.diff_drain = 7;
                b.diff_size = 4;
                b.diff_overall = 8.2f;
                b.diff_approach = 9.3f;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 5.52547f;
            });

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/2499019037
                id = 2499019037,
                user_id = 4502339,
                beatmap_id = beatmap.beatmap_id,
                ruleset_id = 3,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.S,
                passed = true,
                accuracy = 0.992958,
                max_combo = 717,
                total_score = 976025,
                ScoreData = new SoloScoreData
                {
                    Mods = [new APIMod(new ManiaModKey4())],
                    Statistics =
                    {
                        [HitResult.Good] = 5,
                        [HitResult.Great] = 203,
                        [HitResult.Perfect] = 509,
                        [HitResult.IgnoreHit] = 362,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Perfect] = 717,
                        [HitResult.IgnoreHit] = 362,
                    },
                },
                pp = 61.9051f,
                legacy_score_id = null,
                legacy_total_score = 0,
                started_at = new DateTimeOffset(2024, 3, 13, 15, 10, 49, TimeSpan.Zero),
                ended_at = new DateTimeOffset(2024, 3, 13, 15, 12, 10, TimeSpan.Zero),
                build_id = 7478,
            };

            await conn.ExecuteAsync(@"INSERT INTO `osu_builds` (`build_id`, `version`, `stream_id`) VALUES (7478, '2024.312.1-lazer-windows', NULL)");

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", 976025, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 878422, CancellationToken, score);

            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 878422, CancellationToken, score);
        }

        /// <summary>
        /// An osu!mania score set:
        /// <list type="bullet">
        /// <item><b>in lazer</b>,</item>
        /// <item>on a <b>converted beatmap</b>,</item>
        /// <item>with <b>a key mod present</b>,</item>
        /// <item>that <b>does</b> have <see cref="SoloScoreData.TotalScoreWithoutMods"/> populated,</item>
        /// </list>
        /// should have its total score correctly recalculated.
        /// </summary>
        [Fact]
        public async Task TestLazerManiaScoreWithOldKeyModMultiplierAndWithTotalScoreWithoutModsPopulatedSetBeforeNewMultiplierRollout()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 846105;
                b.total_length = 144;
                b.hit_length = 124;
                b.countTotal = 850;
                b.countNormal = 283;
                b.countSlider = 279;
                b.countSpinner = 3;
                b.diff_drain = 7;
                b.diff_size = 4.3f;
                b.diff_overall = 9.3f;
                b.diff_approach = 9.6f;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 6.16647f;
            });

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/5034698668
                id = 5034698668,
                user_id = 37777925,
                beatmap_id = beatmap.beatmap_id,
                ruleset_id = 3,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.S,
                accuracy = 0.996248f,
                max_combo = 1171,
                total_score = 987248,
                ScoreData = new SoloScoreData
                {
                    Mods = [new APIMod(new ManiaModKey4())],
                    Statistics =
                    {
                        [HitResult.Good] = 2,
                        [HitResult.Great] = 226,
                        [HitResult.Perfect] = 943,
                        [HitResult.IgnoreHit] = 772,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Perfect] = 1171,
                        [HitResult.IgnoreHit] = 772,
                    },
                    TotalScoreWithoutMods = 987248,
                },
                pp = 49.8456,
                legacy_score_id = null,
                legacy_total_score = 0,
                started_at = new DateTimeOffset(2025, 6, 21, 5, 58, 31, TimeSpan.Zero),
                ended_at = new DateTimeOffset(2025, 6, 21, 6, 0, 54, TimeSpan.Zero),
                build_id = 8011,
            };

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", 987248, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 888523, CancellationToken, score);

            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 888523, CancellationToken, score);
        }

        /// <summary>
        /// An osu!mania score set:
        /// <list type="bullet">
        /// <item><b>in lazer</b>,</item>
        /// <item>on a <b>converted beatmap</b>,</item>
        /// <item>with <b>a key mod present</b>,</item>
        /// <item>set on a date <b>after</b> the release of https://github.com/ppy/osu/pull/30506,</item>
        /// <item>set on a build <b>before</b> the release of https://github.com/ppy/osu/pull/30506,</item>
        /// </list>
        /// should receive the old multiplier of 1.0x when populating <see cref="SoloScoreData.TotalScoreWithoutMods"/>.
        /// </summary>
        /// <remarks>
        /// The distinction between "date" and "build" here is important because there are known cases of users
        /// continuing to play on the builds with the old mod multiplier for months after the change went live.
        /// </remarks>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task TestLazerManiaScoreWithOldKeyModMultiplierSetAfterNewMultiplierRollout(bool populateTotalScoreWithoutMods)
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 3848418;
                b.total_length = 250;
                b.hit_length = 249;
                b.countTotal = 1220;
                b.countNormal = 511;
                b.countSlider = 350;
                b.countSpinner = 3;
                b.diff_drain = 7;
                b.diff_size = 4;
                b.diff_overall = 7.5f;
                b.diff_approach = 8;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 5.18562f;
            });

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/5991309448
                id = 5991309448,
                user_id = 37777925,
                beatmap_id = beatmap.beatmap_id,
                ruleset_id = 3,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.S,
                passed = true,
                accuracy = 0.997228f,
                max_combo = 1910,
                total_score = 990511,
                ScoreData = new SoloScoreData
                {
                    Mods = [new APIMod(new ManiaModKey4())],
                    Statistics =
                    {
                        [HitResult.Meh] = 1,
                        [HitResult.Good] = 2,
                        [HitResult.Great] = 230,
                        [HitResult.Perfect] = 1677,
                        [HitResult.IgnoreHit] = 1090,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Perfect] = 1910,
                        [HitResult.IgnoreHit] = 1090,
                    },
                    TotalScoreWithoutMods = populateTotalScoreWithoutMods ? 990511 : null,
                },
                pp = 67.1225,
                legacy_score_id = null,
                legacy_total_score = 0,
                started_at = new DateTimeOffset(2026, 1, 1, 10, 50, 26, TimeSpan.Zero),
                ended_at = new DateTimeOffset(2026, 1, 1, 10, 54, 47, TimeSpan.Zero),
                build_id = 8066,
            };

            await conn.ExecuteAsync(@"INSERT INTO `osu_builds` (`build_id`, `version`, `stream_id`) VALUES (8066, '2025.710.0-lazer-windows', NULL)");

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", 990511, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 891460, CancellationToken, score);

            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 891460, CancellationToken, score);
        }

        /// <summary>
        /// An osu!mania score set:
        /// <list type="bullet">
        /// <item><b>in lazer</b>,</item>
        /// <item>on a <b>converted beatmap</b>,</item>
        /// <item>with <b>a key mod present</b>,</item>
        /// <item>that <b>does</b> have <see cref="SoloScoreData.TotalScoreWithoutMods"/> populated,</item>
        /// <item>set on a date <b>after</b> the release of https://github.com/ppy/osu/pull/30506,</item>
        /// <item>set on a build <b>after</b> the release of https://github.com/ppy/osu/pull/30506,</item>
        /// </list>
        /// should have its total score correctly recalculated.
        /// </summary>
        /// <remarks>
        /// It should be temporally impossible to have a score with the new key mod multipliers and without <see cref="SoloScoreData.TotalScoreWithoutMods"/> populated,
        /// since the new key mod multipliers went live in July 2025 (https://github.com/ppy/osu/pull/30506#event-18654025651),
        /// and <see cref="SoloScoreData.TotalScoreWithoutMods"/> started getting stored for incoming scores in July 2024 (https://github.com/ppy/osu-web/pull/11211#event-13533502270).
        /// </remarks>
        [Fact]
        public async Task TestLazerManiaScoreWithNewKeyModMultiplierAndWithTotalScoreWithoutModsPopulated()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 5245944;
            });

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/6655815207
                id = 6655815207,
                user_id = 25915260,
                beatmap_id = beatmap.beatmap_id,
                ruleset_id = 3,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.S,
                passed = true,
                accuracy = 0.988837,
                max_combo = 2357,
                total_score = 865765,
                ScoreData = new SoloScoreData
                {
                    Mods = [new APIMod(new ManiaModKey4())],
                    Statistics =
                    {
                        [HitResult.Ok] = 2,
                        [HitResult.Good] = 40,
                        [HitResult.Great] = 683,
                        [HitResult.Perfect] = 1632,
                        [HitResult.IgnoreHit] = 1180
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Perfect] = 2357,
                        [HitResult.IgnoreHit] = 1180,
                    },
                    TotalScoreWithoutMods = 961961,
                },
                pp = 89.2261f,
                legacy_score_id = null,
                legacy_total_score = 0,
                started_at = new DateTimeOffset(2026, 5, 7, 14, 42, 10, TimeSpan.Zero),
                ended_at = new DateTimeOffset(2026, 5, 7, 14, 45, 59, TimeSpan.Zero),
                build_id = 8686,
            };

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", 961961, CancellationToken, score);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 865765, CancellationToken, score);

            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 865765, CancellationToken, score);
        }

        /// <summary>
        /// Checks that neither processor dies if a beatmap is missing for a score.
        /// </summary>
        [Fact]
        public async Task TestMissingBeatmap()
        {
            using var conn = Processor.GetDatabaseConnection();

            var score = CreateTestScore();
            score.Score.ScoreData.Mods = [new APIMod(new OsuModDoubleTime())];
            InsertScore(conn, score);

            var populateCommand = new PopulateTotalScoreWithoutModsCommand { StartId = score.Score.id };
            await populateCommand.OnExecuteAsync(CancellationToken);

            var recalculateCommand = new RecalculateModMultipliersCommand { StartId = score.Score.id };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 100000, CancellationToken, score.Score);
        }

        [Fact]
        public async Task TestPopulateTotalScoreWithoutModsCommandDoesNothingWhenDryRun()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 2834369;
                b.total_length = 309;
                b.hit_length = 298;
                b.countTotal = 2109;
                b.countNormal = 1568;
                b.countSlider = 266;
                b.countSpinner = 3;
                b.diff_drain = 4.5f;
                b.diff_size = 5.4f;
                b.diff_overall = 9;
                b.diff_approach = 8.5f;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 5.50778f;
            });

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/3176143420
                id = 3176143420,
                user_id = 5182050,
                ruleset_id = 0,
                beatmap_id = beatmap.beatmap_id,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.B,
                passed = true,
                accuracy = 0.859511,
                max_combo = 617,
                total_score = 200732,
                ScoreData = new SoloScoreData
                {
                    Mods = [new APIMod(new OsuModNoFail()), new APIMod(new OsuModDoubleTime())],
                    Statistics =
                    {
                        [HitResult.Ok] = 292,
                        [HitResult.Meh] = 53,
                        [HitResult.Miss] = 36,
                        [HitResult.Great] = 1456,
                        [HitResult.IgnoreHit] = 264,
                        [HitResult.IgnoreMiss] = 22,
                        [HitResult.LargeBonus] = 2,
                        [HitResult.SmallBonus] = 21,
                        [HitResult.LargeTickHit] = 189,
                        [HitResult.LargeTickMiss] = 1,
                        [HitResult.SliderTailHit] = 257,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Great] = 1837,
                        [HitResult.IgnoreHit] = 266,
                        [HitResult.LargeBonus] = 12,
                        [HitResult.SmallBonus] = 22,
                        [HitResult.LargeTickHit] = 190,
                        [HitResult.SliderTailHit] = 266,
                    },
                },
                pp = 171.482f,
                legacy_score_id = null,
                legacy_total_score = 0,
                started_at = new DateTimeOffset(2024, 7, 17, 21, 39, 37, TimeSpan.Zero),
                ended_at = new DateTimeOffset(2024, 7, 17, 21, 43, 5, TimeSpan.Zero),
                build_id = 7596,
            };

            await conn.ExecuteAsync(@"INSERT INTO `osu_builds` (`build_id`, `version`, `stream_id`) VALUES (7596, '2024.625.2-lazer-windows', NULL)");

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var populateCommand = new PopulateTotalScoreWithoutModsCommand
            {
                StartId = score.id,
                DryRun = true,
            };
            await populateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT JSON_VALUE(`data`, '$.total_score_without_mods') FROM `scores` WHERE `id` = @id", (int?)null, CancellationToken, score);
        }

        [Fact]
        public async Task TestRecalculateModMultipliersCommandDoesNothingWhenDryRun()
        {
            using var conn = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 846105;
                b.total_length = 144;
                b.hit_length = 124;
                b.countTotal = 850;
                b.countNormal = 283;
                b.countSlider = 279;
                b.countSpinner = 3;
                b.diff_drain = 7;
                b.diff_size = 4.3f;
                b.diff_overall = 9.3f;
                b.diff_approach = 9.6f;
                b.playmode = 0;
                b.approved = BeatmapOnlineStatus.Ranked;
                b.difficultyrating = 6.16647f;
            });

            var score = new SoloScore
            {
                // https://osu.ppy.sh/scores/5034698668
                id = 5034698668,
                user_id = 37777925,
                beatmap_id = beatmap.beatmap_id,
                ruleset_id = 3,
                has_replay = true,
                preserve = true,
                ranked = true,
                rank = ScoreRank.S,
                accuracy = 0.996248f,
                max_combo = 1171,
                total_score = 987248,
                ScoreData = new SoloScoreData
                {
                    Mods = [new APIMod(new ManiaModKey4())],
                    Statistics =
                    {
                        [HitResult.Good] = 2,
                        [HitResult.Great] = 226,
                        [HitResult.Perfect] = 943,
                        [HitResult.IgnoreHit] = 772,
                    },
                    MaximumStatistics =
                    {
                        [HitResult.Perfect] = 1171,
                        [HitResult.IgnoreHit] = 772,
                    },
                    TotalScoreWithoutMods = 987248,
                },
                pp = 49.8456,
                legacy_score_id = null,
                legacy_total_score = 0,
                started_at = new DateTimeOffset(2025, 6, 21, 5, 58, 31, TimeSpan.Zero),
                ended_at = new DateTimeOffset(2025, 6, 21, 6, 0, 54, TimeSpan.Zero),
                build_id = 8011,
            };

            InsertScore(conn, new ScoreItem(score, new ProcessHistory()));

            var recalculateCommand = new RecalculateModMultipliersCommand
            {
                StartId = score.id,
                DryRun = true,
            };
            await recalculateCommand.OnExecuteAsync(CancellationToken);

            WaitForDatabaseState(@"SELECT `total_score` FROM `scores` WHERE `id` = @id", 987248, CancellationToken, score);
        }
    }
}
