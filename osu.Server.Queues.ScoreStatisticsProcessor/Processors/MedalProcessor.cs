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
        private static readonly List<IMedalAwarder> medal_awarders = new List<IMedalAwarder>();

        private ImmutableArray<Medal>? availableMedals;

        static MedalProcessor()
        {
            // add each processor automagically.
            foreach (var t in typeof(ScoreStatisticsProcessor).Assembly.GetTypes().Where(t => !t.IsInterface && typeof(IMedalAwarder).IsAssignableFrom(t)))
            {
                if (Activator.CreateInstance(t) is IMedalAwarder awarder)
                    medal_awarders.Add(awarder);
            }

            // TODO: ensure that every medal is accounted for.
        }

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            // TODO: don't run for medals the user already has.

            var medals = getAvailableMedals(conn)
                .Where(m => m.mode == null || m.mode == score.RulesetID);

            foreach (var m in medals)
            {
                Console.WriteLine($"Checking medal {m.name}...");

                foreach (var awarder in medal_awarders)
                {
                    if (awarder.Check(score, m, conn))
                    {
                        awardMedal(score, m);
                        break;
                    }
                }
            }
        }

        private IEnumerable<Medal> getAvailableMedals(MySqlConnection conn)
        {
            return availableMedals ??= conn.Query<Medal>("SELECT * FROM osu_achievements WHERE enabled = 1").ToImmutableArray();
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