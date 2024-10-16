// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Computes the performance points of scores.
    /// </summary>
    public class ScorePerformanceProcessor : IProcessor
    {
        public const int ORDER = 0;

        private BeatmapStore? beatmapStore;
        private BuildStore? buildStore;

        private long lastStoreRefresh;

        public int Order => ORDER;

        public bool RunOnFailedScores => false;

        public bool RunOnLegacyScores => true;

        private static readonly bool write_legacy_score_pp = Environment.GetEnvironmentVariable("WRITE_LEGACY_SCORE_PP") != "0";

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            ProcessScoreAsync(score, conn, transaction).Wait();
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }

        /// <summary>
        /// Processes the raw PP value of all scores from a specified user.
        /// </summary>
        /// <param name="userId">The user to process all scores of.</param>
        /// <param name="rulesetId">The ruleset for which scores should be processed.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public async Task ProcessUserScoresAsync(uint userId, int rulesetId, MySqlConnection connection, MySqlTransaction? transaction = null, CancellationToken cancellationToken = default)
        {
            var scores = (await connection.QueryAsync<SoloScore>("SELECT * FROM scores WHERE `user_id` = @UserId AND `ruleset_id` = @RulesetId", new
            {
                UserId = userId,
                RulesetId = rulesetId
            }, transaction: transaction)).ToArray();

            Console.WriteLine($"Processing {userId} ({scores.Length} scores)...");

            foreach (SoloScore score in scores)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                await ProcessScoreAsync(score, connection, transaction);
            }
        }

        /// <summary>
        /// Processes the raw PP value of a given score.
        /// </summary>
        /// <param name="scoreId">The score to process.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task ProcessScoreAsync(ulong scoreId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            var score = await connection.QuerySingleOrDefaultAsync<SoloScore>("SELECT * FROM scores WHERE `id` = @ScoreId", new
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
        public async Task ProcessScoreAsync(SoloScore score, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            // Usually checked via "RunOnFailedScores", but this method is also used by the CLI batch processor.
            if (!score.passed)
                return;

            long currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (buildStore == null || beatmapStore == null || currentTimestamp - lastStoreRefresh > 60_000)
            {
                buildStore = await BuildStore.CreateAsync(connection, transaction);
                beatmapStore = await BeatmapStore.CreateAsync(connection, transaction);

                lastStoreRefresh = currentTimestamp;
            }

            try
            {
                score.beatmap ??= (await beatmapStore.GetBeatmapAsync(score.beatmap_id, connection, transaction));

                if (score.beatmap is not Beatmap beatmap)
                    return;

                if (!beatmapStore.IsBeatmapValidForPerformance(beatmap, score.ruleset_id))
                    return;

                Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);
                Mod[] mods = score.ScoreData.Mods.Select(m => m.ToMod(ruleset)).ToArray();

                if (!AllModsValidForPerformance(score, mods))
                    return;

                DifficultyAttributes? difficultyAttributes = await beatmapStore.GetDifficultyAttributesAsync(beatmap, ruleset, mods, connection, transaction);

                // Performance needs to be allowed for the build.
                // legacy scores don't need a build id
                if (score.legacy_score_id == null && (score.build_id == null || buildStore.GetBuild(score.build_id.Value)?.allow_performance != true))
                    return;

                if (difficultyAttributes == null)
                    return;

                ScoreInfo scoreInfo = score.ToScoreInfo();
                PerformanceAttributes? performanceAttributes = ruleset.CreatePerformanceCalculator()?.Calculate(scoreInfo, difficultyAttributes);

                if (performanceAttributes == null)
                    return;

                await connection.ExecuteAsync("UPDATE scores SET pp = @Pp WHERE id = @ScoreId", new
                {
                    ScoreId = score.id,
                    Pp = performanceAttributes.Total
                }, transaction: transaction);

                // for the following code to take effect, `RunOnLegacyScores` must also be true (currently is false).
                if (score.is_legacy_score && write_legacy_score_pp)
                {
                    var helper = LegacyDatabaseHelper.GetRulesetSpecifics(score.ruleset_id);
                    await connection.ExecuteAsync($"UPDATE {helper.HighScoreTable} SET pp = @Pp WHERE score_id = @LegacyScoreId", new
                    {
                        LegacyScoreId = score.legacy_score_id,
                        Pp = performanceAttributes.Total,
                    }, transaction: transaction);
                }
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"{score.id} failed with: {ex}");
            }
        }

        /// <summary>
        /// Checks whether all mods in a given array are valid to give PP for.
        /// </summary>
        public static bool AllModsValidForPerformance(SoloScore score, Mod[] mods)
        {
            IEnumerable<Mod> modsToCheck = mods;

            // Classic mod is only allowed on legacy scores.
            if (score.is_legacy_score)
                modsToCheck = mods.Where(mod => mod is not ModClassic);

            return modsToCheck.All(m => m.Ranked);
        }
    }
}
