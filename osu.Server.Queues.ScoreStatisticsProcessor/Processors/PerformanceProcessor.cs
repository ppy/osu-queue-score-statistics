// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using MySqlConnector;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    public class PerformanceProcessor : IProcessor
    {
        private static readonly List<Ruleset> available_rulesets = getRulesets();

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            Ruleset ruleset = available_rulesets.Single(r => r.RulesetInfo.ID == score.ruleset_id);
            Mod[] mods = score.mods.Select(m => m.ToMod(ruleset)).ToArray();
            ScoreInfo scoreInfo = score.ToScoreInfo(mods);

            double performance = computePerformance(ruleset, mods, scoreInfo, conn, transaction);

            conn.Execute("INSERT INTO solo_scores_performance (`score_id`, `pp`) VALUES (@ScoreId, @PP) ON DUPLICATE KEY UPDATE `pp` = @PP", new
            {
                ScoreId = score.id,
                PP = performance
            }, transaction);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
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
            LegacyMods legacyModValue = LegacyModsHelper.MaskRelevantMods(ruleset.ConvertToLegacyMods(mods), score.RulesetID != beatmap.playmode);

            var rawDifficultyAttribs = conn.Query<BeatmapDifficultyAttribute>(
                "SELECT * FROM osu_beatmap_difficulty_attribs WHERE beatmap_id = @BeatmapId AND mode = @RulesetId AND mods = @ModValue", new
                {
                    BeatmapId = score.BeatmapInfo.OnlineID,
                    RulesetId = score.RulesetID,
                    ModValue = (uint)legacyModValue
                }, transaction).ToArray();

            var difficultyAttributes = rawDifficultyAttribs.ToDictionary(a => (int)a.attrib_id).Map(score.RulesetID, beatmap);
            var performanceCalculator = ruleset.CreatePerformanceCalculator(difficultyAttributes, score);
            return performanceCalculator.Calculate();
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

        private static List<Ruleset> getRulesets()
        {
            const string ruleset_library_prefix = "osu.Game.Rulesets";

            var rulesetsToProcess = new List<Ruleset>();

            foreach (string file in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, $"{ruleset_library_prefix}.*.dll"))
            {
                try
                {
                    var assembly = Assembly.LoadFrom(file);
                    Type type = assembly.GetTypes().First(t => t.IsPublic && t.IsSubclassOf(typeof(Ruleset)));
                    rulesetsToProcess.Add((Ruleset)Activator.CreateInstance(type)!);
                }
                catch
                {
                    throw new Exception($"Failed to load ruleset ({file})");
                }
            }

            return rulesetsToProcess;
        }
    }
}
