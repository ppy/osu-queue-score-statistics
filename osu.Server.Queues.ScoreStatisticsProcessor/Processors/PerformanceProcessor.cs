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
using osu.Game.Rulesets.Mods;
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
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
            Ruleset ruleset = available_rulesets.Single(r => r.RulesetInfo.ID == score.ruleset_id);
            Mod[] mods = score.mods.Select(m => m.ToMod(ruleset)).ToArray();
            ScoreInfo scoreInfo = score.ToScoreInfo(mods);

            // Todo: We shouldn't be using legacy mods, but this requires difficulty calculation to be performed in-line.
            LegacyMods legacyModValue = LegacyModsHelper.MaskRelevantMods(ruleset.ConvertToLegacyMods(mods));

            var beatmap = conn.QuerySingleOrDefault<Beatmap?>("SELECT * FROM osu_beatmaps WHERE beatmap_id = @BeatmapId", new
            {
                BeatmapId = score.beatmap_id
            });

            // Todo: REMOVE THIS. This should never be the case (testing-only).
            if (beatmap == null)
                return;

            var rawDifficultyAttribs = conn.Query<BeatmapDifficultyAttribute>(
                "SELECT * FROM osu_beatmap_difficulty_attribs WHERE beatmap_id = @BeatmapId AND mode = @RulesetId AND mods = @ModValue", new
                {
                    BeatmapId = score.beatmap_id,
                    RulesetId = score.ruleset_id,
                    ModValue = (uint)legacyModValue
                })?.ToArray();

            // Todo: REMOVE THIS. This should never be the case (testing-only).
            if (rawDifficultyAttribs == null || rawDifficultyAttribs.Length == 0)
                return;

            var difficultyAttributes = rawDifficultyAttribs.ToDictionary(a => (int)a.attrib_id).Map(score.ruleset_id, beatmap);

            var performanceCalculator = ruleset.CreatePerformanceCalculator(difficultyAttributes, scoreInfo);
            var performance = performanceCalculator.Calculate();

            conn.Execute("INSERT INTO solo_scores_v2_performance (`score_id`, `pp`) VALUES (@ScoreId, @Pp) ON DUPLICATE KEY UPDATE `pp` = @Pp", new
            {
                ScoreId = score.id,
                Pp = performance
            });
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
