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
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
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

        // For testing purposes.
        public static Action<AwardedMedal>? MedalAwarded;

        static MedalProcessor()
        {
            // add each processor automagically.
            foreach (var t in typeof(ScoreStatisticsQueueProcessor).Assembly.GetTypes().Where(t => !t.IsInterface && typeof(IMedalAwarder).IsAssignableFrom(t)))
            {
                if (Activator.CreateInstance(t) is IMedalAwarder awarder)
                    medal_awarders.Add(awarder);
            }

            // TODO: ensure that every medal is accounted for.
        }

        public bool RunOnFailedScores => true; // This is handled by each awarder.

        public bool RunOnLegacyScores => false;

        // This processor needs to run after the play count and hit statistics have been applied.
        public int Order => Math.Max(PlayCountProcessor.ORDER, HitStatisticsProcessor.ORDER) + 1;

        public void RevertFromUserStats(SoloScoreInfo score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction)
        {
        }

        public void ApplyToUserStats(SoloScoreInfo score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction)
        {
            int[] alreadyAchieved = conn.Query<int>("SELECT achievement_id FROM osu_user_achievements WHERE user_id = @userId", new
            {
                userId = score.UserID
            }, transaction: transaction).ToArray();

            var availableMedalsForUser = getAvailableMedals(conn, transaction)
                                         .Where(m => m.mode == null || m.mode == score.RulesetID)
                                         .Where(m => !alreadyAchieved.Contains(m.achievement_id))
                                         .ToArray();

            foreach (var awarder in medal_awarders)
            {
                if (!score.Passed && !awarder.RunOnFailedScores)
                    continue;

                foreach (var awardedMedal in awarder.Check(score, availableMedalsForUser, conn, transaction))
                {
                    awardMedal(score, awardedMedal);
                    break;
                }
            }
        }

        private IEnumerable<Medal> getAvailableMedals(MySqlConnection conn, MySqlTransaction transaction)
        {
            return availableMedals ??= conn.Query<Medal>("SELECT * FROM osu_achievements WHERE enabled = 1", transaction: transaction).ToImmutableArray();
        }

        private void awardMedal(SoloScoreInfo score, Medal medal)
        {
            // Perform LIO request to award the medal.
            Console.WriteLine($"Awarding medal {medal.name} to user {score.UserID} (score {score.ID})");
            LegacyDatabaseHelper.RunLegacyIO($"user-achievement/{score.UserID}/{medal.achievement_id}/{score.BeatmapID}", "POST");
            MedalAwarded?.Invoke(new AwardedMedal(medal, score));
        }

        public void ApplyGlobal(SoloScoreInfo score, MySqlConnection conn)
        {
        }

        public record struct AwardedMedal(Medal Medal, SoloScoreInfo Score);
    }
}
