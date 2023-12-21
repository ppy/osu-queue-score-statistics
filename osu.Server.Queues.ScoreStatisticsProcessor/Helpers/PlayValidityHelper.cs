// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Linq;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class PlayValidityHelper
    {
        /// <summary>
        /// Whether the supplied <paramref name="score"/> is valid for tracking total play time and count.
        /// </summary>
        /// <seealso cref="PlayCountProcessor"/>
        /// <seealso cref="PlayTimeProcessor"/>
        /// <param name="score">The score to check.</param>
        /// <param name="lengthInSeconds">The total length of play in seconds in the supplied <paramref name="score"/>.</param>
        public static bool IsValidForPlayTracking(this SoloScoreInfo score, out int lengthInSeconds)
        {
            lengthInSeconds = GetPlayLength(score);

            int totalObjectsJudged = score.Statistics.Where(kv => kv.Key.IsScorable()).Sum(kv => kv.Value);
            int totalObjects = score.MaximumStatistics.Where(kv => kv.Key.IsScorable()).Sum(kv => kv.Value);

            return lengthInSeconds >= 8
                   && score.TotalScore >= 5000
                   && totalObjectsJudged >= Math.Min(0.1f * totalObjects, 20);
        }

        public static int GetPlayLength(SoloScoreInfo score)
        {
            // to ensure sanity, first get the maximum time feasible from the beatmap's length
            double totalLengthSeconds = score.Beatmap!.Length;

            Ruleset ruleset = ScoreStatisticsQueueProcessor.AVAILABLE_RULESETS.Single(r => r.RulesetInfo.OnlineID == score.RulesetID);

            var rateAdjustMods = score.Mods.Select(m => m.ToMod(ruleset)).OfType<ModRateAdjust>().ToArray();

            foreach (var mod in rateAdjustMods)
                totalLengthSeconds /= mod.SpeedChange.Value;

            Debug.Assert(score.StartedAt != null);

            // TODO: better handle failed plays once we have incoming data.

            TimeSpan realTimePassed = score.EndedAt - score.StartedAt.Value;
            return (int)Math.Min(totalLengthSeconds, realTimePassed.TotalSeconds);
        }
    }
}
