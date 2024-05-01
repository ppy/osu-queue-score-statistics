// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
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
        public static bool IsValidForPlayTracking(this SoloScore score, out int lengthInSeconds)
        {
            lengthInSeconds = GetPlayLength(score);

            int totalObjectsJudged = score.ScoreData.Statistics.Where(kv => kv.Key.IsScorable()).Sum(kv => kv.Value);
            int totalObjects = score.ScoreData.MaximumStatistics.Where(kv => kv.Key.IsScorable()).Sum(kv => kv.Value);

            return score.passed
                   || (lengthInSeconds >= 8
                       && score.total_score >= 5000
                       && totalObjectsJudged >= Math.Min(0.1f * totalObjects, 20));
        }

        /// <summary>
        /// Returns <see langword="true"/> if the beatmap on which <paramref name="score"/> was set
        /// is valid to be included in ranked counts, such as total ranked score and user rank (grade) counts.
        /// </summary>
        /// <seealso cref="RankedScoreProcessor"/>
        /// <seealso cref="UserRankCountProcessor"/>
        /// <seealso cref="ManiaKeyModeUserStatsProcessor"/>
        public static bool BeatmapValidForRankedCounts(this SoloScore score)
        {
            // see https://osu.ppy.sh/wiki/en/Gameplay/Score/Ranked_score
            var status = score.beatmap?.approved;
            return status == BeatmapOnlineStatus.Ranked
                   || status == BeatmapOnlineStatus.Approved
                   || status == BeatmapOnlineStatus.Loved;
        }

        /// <summary>
        /// Returns the length of play in the given <paramref name="score"/> in seconds.
        /// </summary>
        public static int GetPlayLength(SoloScore score)
        {
            // to ensure sanity, first get the maximum time feasible from the beatmap's length
            double totalLengthSeconds = score.beatmap!.total_length;

            Ruleset ruleset = ScoreStatisticsQueueProcessor.AVAILABLE_RULESETS.Single(r => r.RulesetInfo.OnlineID == score.ruleset_id);

            var rateAdjustMods = score.ScoreData.Mods.Select(m => m.ToMod(ruleset)).OfType<ModRateAdjust>().ToArray();

            foreach (var mod in rateAdjustMods)
                totalLengthSeconds /= mod.SpeedChange.Value;

            Debug.Assert(score.started_at != null);

            // TODO: better handle failed plays once we have incoming data.

            TimeSpan realTimePassed = score.ended_at - score.started_at.Value;
            return (int)Math.Min(totalLengthSeconds, realTimePassed.TotalSeconds);
        }
    }
}
