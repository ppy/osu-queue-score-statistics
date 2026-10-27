// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Framework.Extensions;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;
using StatsdClient;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Computes the performance points of scores.
    /// </summary>
    public class ScorePerformanceProcessor : IProcessor
    {
        private static readonly bool check_client_version = Environment.GetEnvironmentVariable("CLIENT_CHECK_VERSION") != "0";

        public const int ORDER = 0;

        private BuildStore? buildStore;

        public int Order => ORDER;

        public bool RunOnFailedScores => false;

        public bool RunOnLegacyScores => true;

        public bool Verbose { get; set; }

        private static readonly bool write_legacy_score_pp = Environment.GetEnvironmentVariable("WRITE_LEGACY_SCORE_PP") != "0";

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction, List<Action<ProcessorContext>> postTransactionActions,
                                        DogStatsdService dogStatsd)
        {
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction, List<Action<ProcessorContext>> postTransactionActions, DogStatsdService dogStatsd)
        {
            double? valueBeforeProcessing = score.pp;

            try
            {
                if (ProcessScoreAsync(score, conn, transaction).GetResultSafely())
                {
                    if (score.is_legacy_score && write_legacy_score_pp)
                    {
                        var helper = LegacyDatabaseHelper.GetRulesetSpecifics(score.ruleset_id);
                        conn.Execute($"UPDATE scores SET pp = @Pp WHERE id = @ScoreId; UPDATE {helper.HighScoreTable} SET pp = @Pp WHERE score_id = @LegacyScoreId", new
                        {
                            ScoreId = score.id,
                            LegacyScoreId = score.legacy_score_id,
                            Pp = score.pp,
                        }, transaction: transaction);
                    }
                    else
                    {
                        conn.Execute("UPDATE scores SET pp = @Pp WHERE id = @ScoreId", new
                        {
                            ScoreId = score.id,
                            Pp = score.pp,
                        }, transaction: transaction);
                    }
                }
            }
            catch
            {
                // WARNING: PP value must be restored if anything fails here.
                //
                // The reason for this is to cover off the case wherein the database writes FAIL.
                // In such a case, the score will be re-queued for processing again to cover off transient failures -
                // however, the re-queue process takes the `score` model verbatim, RE-SERIALISES IT to JSON, and then re-enqueues THAT.
                // this means that if the write below is permitted to occur BEFORE the value is written to database,
                // on the next retry the state of the score will be INCONSISTENT with the database
                // because the database write of pp HAS NOT ACTUALLY HAPPENED
                // but the score model as written to and then read from redis WILL HAVE PP POPULATED.
                score.pp = valueBeforeProcessing;
            }
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }

        /// <summary>
        /// Processes the raw PP value of a given score, updating it if required.
        /// </summary>
        /// <remarks>
        /// There are many preconditions that need to be satisfied for an update to occur:
        /// - Is a passing score
        /// - Has a beatmap attached
        /// - Is not blacklisted for pp attribution
        /// - Mods are value for pp purposes
        /// - Score is set on a client build which is allowed to submit pp gaining scores
        /// - PP value changed by over 0.1 from any existing value.
        /// </remarks>
        /// <param name="score">The score to process.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        public async Task<bool> ProcessScoreAsync(SoloScore score, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            // Usually checked via "RunOnFailedScores", but this method is also used by the CLI batch processor.
            if (!score.passed)
                return false;

            buildStore ??= new BuildStore();

            score.beatmap ??= await BeatmapStore.GetBeatmapAsync(score.beatmap_id, connection, transaction);

            if (score.beatmap is not Beatmap beatmap)
                return false;

            // TODO: will fail for newly ranked beatmaps for up to one minute (beatmap store purge).
            if (!BeatmapStore.IsBeatmapValidForPerformance(beatmap, score.ruleset_id))
                return false;

            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.ruleset_id);
            Mod[] mods = score.ScoreData.Mods.Select(m => m.ToMod(ruleset)).ToArray();

            if (!AllModsValidForPerformance(score, mods))
                return false;

            // Performance needs to be allowed for the build.
            // legacy scores don't need a build id
            if (check_client_version && score.legacy_score_id == null
                                     && (score.build_id == null || (await buildStore.GetBuildAsync(score.build_id.Value, connection, transaction))?.allow_performance != true))
                return false;

            DifficultyAttributes difficultyAttributes = await BeatmapStore.GetDifficultyAttributesAsync(beatmap, ruleset, mods, connection, transaction);
            PerformanceAttributes? performanceAttributes = ruleset.CreatePerformanceCalculator()?.Calculate(score.ToScoreInfo(), difficultyAttributes);

            if (performanceAttributes == null)
                return false;

            if (score.pp != null && Math.Abs(score.pp.Value - performanceAttributes.Total) < 0.1)
                return false;

            if (Verbose)
            {
                Console.WriteLine(
                    $"{score.id.ToString(),-12}: {score.pp ?? -1,-4:N2} -> {performanceAttributes.Total,-4:N2} "
                    + $"({performanceAttributes.Total - (score.pp ?? 0),-5:+#,0.00;-#,0.00;+#,0.00})"
                    + (score.is_legacy_score ? " LEGACY" : string.Empty));
            }

            score.pp = performanceAttributes.Total;
            return true;
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
