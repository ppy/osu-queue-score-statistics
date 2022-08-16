// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Adjust max combo (if required) for the user.
    /// </summary>
    [UsedImplicitly]
    public class MaxComboProcessor : IProcessor
    {
        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
            // TODO: this will require access to stable scores to be implemented correctly.
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            Ruleset ruleset = ScoreStatisticsProcessor.AVAILABLE_RULESETS.Single(r => r.RulesetInfo.OnlineID == score.RulesetID);

            // Automation mods should not count towards max combo statistic.
            if (score.Mods.Select(m => m.ToMod(ruleset)).Any(m => m.Type == ModType.Automation))
                return;

            var beatmap = conn.QuerySingleOrDefault<Beatmap?>($"SELECT * FROM {Beatmap.TABLE_NAME} WHERE `beatmap_id` = @BeatmapId", new
            {
                BeatmapId = score.BeatmapID
            }, transaction: transaction);

            if (beatmap == null || beatmap.approved < BeatmapOnlineStatus.Ranked)
                return;

            // TODO: assert the user's score is not higher than the max combo for the beatmap.
            userStats.max_combo = Math.Max(userStats.max_combo, (ushort)score.MaxCombo);
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }
    }
}
