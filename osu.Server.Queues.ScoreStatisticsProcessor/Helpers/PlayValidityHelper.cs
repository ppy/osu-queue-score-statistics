// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public class PlayValidityHelper
    {
        public static int GetPlayLength(SoloScoreInfo score)
        {
            // to ensure sanity, first get the maximum time feasible from the beatmap's length
            double totalLengthSeconds = score.Beatmap!.Length;

            Ruleset ruleset = ScoreStatisticsQueueProcessor.AVAILABLE_RULESETS.Single(r => r.RulesetInfo.OnlineID == score.RulesetID);

            var rateAdjustMods = score.Mods.Select(m => m.ToMod(ruleset)).OfType<ModRateAdjust>().ToArray();

            foreach (var mod in rateAdjustMods)
                totalLengthSeconds /= mod.SpeedChange.Value;

            if (score.StartedAt == null)
                return (int)totalLengthSeconds;

            // TODO: better handle failed plays once we have incoming data.

            TimeSpan realTimePassed = score.EndedAt - score.StartedAt.Value;
            return (int)Math.Min(totalLengthSeconds, realTimePassed.TotalSeconds);
        }
    }
}
