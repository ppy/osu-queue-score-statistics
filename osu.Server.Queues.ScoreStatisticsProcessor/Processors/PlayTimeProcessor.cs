// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Increment total user play time.
    /// </summary>
    [UsedImplicitly]
    public class PlayTimeProcessor : IProcessor
    {
        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            if (previousVersion >= 6)
                userStats.total_seconds_played -= getPlayLength(score, conn, transaction);
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            userStats.total_seconds_played += getPlayLength(score, conn, transaction);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        private static int getPlayLength(SoloScoreInfo score, MySqlConnection conn, MySqlTransaction transaction)
        {
            // to ensure sanity, first get the maximum time feasible from the beatmap's length
            double totalLengthSeconds = conn.QueryFirstOrDefault<double>("SELECT total_length FROM osu_beatmaps WHERE beatmap_id = @beatmap_id", new
            {
                beatmap_id = score.BeatmapID
            }, transaction);

            Ruleset ruleset = ScoreStatisticsProcessor.AVAILABLE_RULESETS.Single(r => r.RulesetInfo.OnlineID == score.RulesetID);

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
