// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Award the medals.
    /// </summary>
    [UsedImplicitly]
    public class MedalProcessor : IProcessor
    {
        private ImmutableArray<Medal>? availableMedals;

        private IEnumerable<Medal> getAvailableMedals(MySqlConnection conn)
        {
            return availableMedals ??= conn.Query<Medal>("SELECT * FROM osu_achievements WHERE enabled = 1").ToImmutableArray();
        }

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            var medals = getAvailableMedals(conn)
                .Where(m => m.mode == null || m.mode == score.RulesetID);

            foreach (var m in medals)
            {
                Console.WriteLine($"Checking medal {m.name}...");

                bool shouldAward = false;

                // process the medals

                if (shouldAward)
                    awardMedal(score, m);
            }
        }

        private void awardMedal(SoloScoreInfo score, Medal medal)
        {
            // Perform LIO request to award the medal.
            Console.WriteLine($"Awarding medal {medal.name} to user {score.UserID} (score {score.ID})");
            LegacyDatabaseHelper.RunLegacyIO($"user-achievement/{score.UserID}/{medal.achievement_id}/{score.BeatmapID}", "POST");
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }
    }
}
