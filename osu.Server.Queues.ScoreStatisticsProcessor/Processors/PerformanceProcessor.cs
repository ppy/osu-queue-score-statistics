// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    public class PerformanceProcessor : IProcessor
    {
        private BeatmapStore? beatmapStore;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            Ruleset ruleset = ScoreStatisticsProcessor.AVAILABLE_RULESETS.Single(r => r.RulesetInfo.OnlineID == score.ruleset_id);
            Mod[] mods = score.mods.Select(m => m.ToMod(ruleset)).ToArray();
            ScoreInfo scoreInfo = score.ToScoreInfo(mods);

            double performance = computePerformance(ruleset, mods, scoreInfo, conn, transaction);

            conn.Execute($"INSERT INTO {SoloScorePerformance.TABLE_NAME} (`score_id`, `pp`) VALUES (@ScoreId, @PP) ON DUPLICATE KEY UPDATE `pp` = @PP", new
            {
                ScoreId = score.id,
                PP = performance
            }, transaction);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        /// <summary>
        /// Processes the raw PP value of all scores from a specified user.
        /// </summary>
        /// <param name="userId">The user to process all scores of.</param>
        /// <param name="rulesetId">The ruleset for which scores should be processed.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task ProcessUserScoresAsync(int userId, int rulesetId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            var scores = (await connection.QueryAsync<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE `user_id` = @UserId AND `ruleset_id` = @RulesetId", new
            {
                UserId = userId,
                RulesetId = rulesetId
            }, transaction: transaction)).ToArray();

            foreach (SoloScore score in scores)
                await ProcessScoreAsync(score, connection, transaction);
        }

        /// <summary>
        /// Processes the raw PP value of a given score.
        /// </summary>
        /// <param name="scoreId">The score to process.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task ProcessScoreAsync(ulong scoreId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            var score = await connection.QuerySingleOrDefaultAsync<SoloScore>($"SELECT * FROM {SoloScore.TABLE_NAME} WHERE `id` = @ScoreId", new
            {
                ScoreId = scoreId
            }, transaction: transaction);

            if (score == null)
            {
                await Console.Error.WriteLineAsync($"Could not find score ID {scoreId}.");
                return;
            }

            await ProcessScoreAsync(score, connection, transaction);
        }

        /// <summary>
        /// Processes the raw PP value of a given score.
        /// </summary>
        /// <param name="score">The score to process.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public Task ProcessScoreAsync(SoloScore score, MySqlConnection connection, MySqlTransaction? transaction = null) => ProcessScoreAsync(score.ScoreInfo, connection, transaction);

        /// <summary>
        /// Processes the raw PP value of a given score.
        /// </summary>
        /// <param name="score">The score to process.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task ProcessScoreAsync(SoloScoreInfo score, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            beatmapStore ??= await BeatmapStore.CreateAsync(connection, transaction);

            try
            {
                Beatmap? beatmap = await beatmapStore.GetBeatmapAsync(score.beatmap_id, connection, transaction);

                if (beatmap == null)
                    return;

                if (!beatmapStore.IsBeatmapValidForPerformance(beatmap, score.ruleset_id))
                    return;

                Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);
                Mod[] mods = score.mods.Select(m => m.ToMod(ruleset)).ToArray();

                DifficultyAttributes? difficultyAttributes = await beatmapStore.GetDifficultyAttributesAsync(beatmap, ruleset, mods, connection, transaction);
                if (difficultyAttributes == null)
                    return;

                ScoreInfo scoreInfo = score.ToScoreInfo(mods);
                PerformanceAttributes? performanceAttributes = ruleset.CreatePerformanceCalculator()?.Calculate(scoreInfo, difficultyAttributes);
                if (performanceAttributes == null)
                    return;

                await connection.ExecuteAsync($"INSERT INTO {SoloScorePerformance.TABLE_NAME} (`score_id`, `pp`) VALUES (@ScoreId, @Pp) ON DUPLICATE KEY UPDATE `pp` = @Pp", new
                {
                    ScoreId = score.id,
                    Pp = performanceAttributes.Total
                }, transaction: transaction);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"{score.id} failed with: {ex}");
            }
        }

        private double computePerformance(Ruleset ruleset, Mod[] mods, ScoreInfo score, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (!AllModsValidForPerformance(mods))
                return 0;

            var beatmap = conn.QuerySingle<Beatmap>("SELECT * FROM osu_beatmaps WHERE beatmap_id = @BeatmapId", new
            {
                BeatmapId = score.BeatmapInfo.OnlineID
            }, transaction);

            // Todo: We shouldn't be using legacy mods, but this requires difficulty calculation to be performed in-line.
            LegacyMods legacyModValue = LegacyModsHelper.MaskRelevantMods(ruleset.ConvertToLegacyMods(mods), score.RulesetID != beatmap.playmode, score.RulesetID);

            var rawDifficultyAttribs = conn.Query<BeatmapDifficultyAttribute>(
                "SELECT * FROM osu_beatmap_difficulty_attribs WHERE beatmap_id = @BeatmapId AND mode = @RulesetId AND mods = @ModValue", new
                {
                    BeatmapId = score.BeatmapInfo.OnlineID,
                    RulesetId = score.RulesetID,
                    ModValue = (uint)legacyModValue
                }, transaction).ToArray();

            DifficultyAttributes attributes = LegacyRulesetHelper.CreateDifficultyAttributes(score.RulesetID);
            attributes.FromDatabaseAttributes(rawDifficultyAttribs.ToDictionary(a => (int)a.attrib_id, a => (double)a.value), beatmap.ToAPIBeatmap());

            return ruleset.CreatePerformanceCalculator()?.Calculate(score, attributes).Total ?? 0;
        }

        /// <summary>
        /// Checks whether all mods in a given array are valid to give PP for.
        /// </summary>
        public static bool AllModsValidForPerformance(Mod[] mods)
        {
            foreach (var m in mods)
            {
                switch (m)
                {
                    case ManiaModHardRock:
                    case ManiaModKey1:
                    case ManiaModKey2:
                    case ManiaModKey3:
                    case ManiaModKey10:
                        return false;

                    case ModEasy:
                    case ModNoFail:
                    case ModHalfTime:
                    case ModSuddenDeath:
                    case ModPerfect:
                    case ModHardRock:
                    case ModDoubleTime:
                    case ModHidden:
                    case ModFlashlight:
                    case ModMuted:
                    case ModClassic:
                    case OsuModSpunOut:
                    case ManiaKeyMod:
                    case ManiaModMirror:
                        continue;

                    default:
                        return false;
                }
            }

            return true;
        }
    }
}
