// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using Dapper;
using MySqlConnector;
using Newtonsoft.Json;
using osu.Framework.IO.Network;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch.Difficulty;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mania.Difficulty;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Taiko.Difficulty;
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

            double performance = computePerformance(score, ruleset, mods, scoreInfo);

            conn.Execute("INSERT INTO solo_scores_performance (`score_id`, `pp`) VALUES (@ScoreId, @PP) ON DUPLICATE KEY UPDATE `pp` = @PP", new
            {
                ScoreId = score.id,
                PP = performance
            }, transaction);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        private double computePerformance(SoloScoreInfo rawScore, Ruleset ruleset, Mod[] mods, ScoreInfo score)
        {
            if (!AllModsValidForPerformance(mods))
                return 0;

            var attributes = queryAttributes(rawScore);
            if (attributes == null)
                return 0;

            return ruleset.CreatePerformanceCalculator(attributes, score)?.Calculate() ?? 0;
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

        private static DifficultyAttributes? queryAttributes(SoloScoreInfo score)
        {
            var req = new WebRequest(AppSettings.DIFFCALC_ENDPOINT)
            {
                Method = HttpMethod.Post,
                ContentType = "application/json",
                Timeout = int.MaxValue,
                AllowInsecureRequests = true
            };

            req.AddRaw(JsonConvert.SerializeObject(new DifficultyAttributesRequestData
            {
                BeatmapId = score.beatmap_id,
                RulesetId = score.ruleset_id,
                Mods = score.mods
            }));

            DifficultyAttributes? attributes = null;

            req.Finished += () =>
            {
                if (req.ResponseStream == null)
                    return;

                switch (score.ruleset_id)
                {
                    case 0:
                        attributes = JsonConvert.DeserializeObject<OsuDifficultyAttributes>(req.GetResponseString()!);
                        break;

                    case 1:
                        attributes = JsonConvert.DeserializeObject<TaikoDifficultyAttributes>(req.GetResponseString()!);
                        break;

                    case 2:
                        attributes = JsonConvert.DeserializeObject<CatchDifficultyAttributes>(req.GetResponseString()!);
                        break;

                    case 3:
                        attributes = JsonConvert.DeserializeObject<ManiaDifficultyAttributes>(req.GetResponseString()!);
                        break;
                }
            };

            req.Perform();

            return attributes;
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

        private class DifficultyAttributesRequestData
        {
            [JsonProperty("beatmap_id")]
            public int BeatmapId { get; set; }

            [JsonProperty("ruleset_id")]
            public int RulesetId { get; set; }

            [JsonProperty("mods")]
            public List<APIMod>? Mods { get; set; }
        }
    }
}
