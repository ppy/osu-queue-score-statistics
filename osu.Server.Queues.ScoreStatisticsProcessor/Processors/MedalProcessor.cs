// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Dapper;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Framework.Extensions.TypeExtensions;
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
            foreach (var t in AppDomain.CurrentDomain
                                       .GetAssemblies()
                                       .SelectMany(t => t.GetTypes())
                                       .Where(t => !t.IsInterface && typeof(IMedalAwarder).IsAssignableFrom(t)))
            {
                if (Activator.CreateInstance(t) is IMedalAwarder awarder)
                    medal_awarders.Add(awarder);
            }

            // TODO: ensure that every medal is accounted for.
        }

        public bool RunOnFailedScores => true; // This is handled by each awarder.

        public bool RunOnLegacyScores => true; // This is handled by each awarder.

        // This processor needs to run after the play count and hit statistics have been applied, at very least.
        public int Order => int.MaxValue - 1;

        public void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions)
        {
        }

        public void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions)
        {
            if (score.beatmap!.approved <= 0)
                return;

            if (DatabaseHelper.IsUserRestricted(conn, userStats.user_id, transaction))
                return;

            int[] alreadyAchieved = conn.Query<int>("SELECT achievement_id FROM osu_user_achievements WHERE user_id = @userId", new
            {
                userId = score.user_id
            }, transaction: transaction).ToArray();

            var availableMedalsForUser = getAvailableMedals(conn, transaction)
                                         .Where(m => m.mode == null || m.mode == score.ruleset_id)
                                         .Where(m => !alreadyAchieved.Contains(m.achievement_id))
                                         .ToArray();

            var dailyChallengeUserStats = conn.QuerySingleOrDefault<DailyChallengeUserStats>(
                @"SELECT * FROM `daily_challenge_user_stats` WHERE `user_id` = @user_id",
                new
                {
                    user_id = userStats.user_id
                },
                transaction) ?? new DailyChallengeUserStats();

            var context = new MedalAwarderContext(score, userStats, dailyChallengeUserStats, conn, transaction);

            foreach (var awarder in medal_awarders)
            {
                if (!score.passed && !awarder.RunOnFailedScores)
                    continue;

                if (score.is_legacy_score && !awarder.RunOnLegacyScores)
                    continue;

                foreach (var awardedMedal in awarder.Check(availableMedalsForUser, context))
                {
                    postTransactionActions.Add(() => awardMedal(score, awardedMedal));
                }
            }
        }

        private IEnumerable<Medal> getAvailableMedals(MySqlConnection conn, MySqlTransaction transaction)
        {
            return availableMedals ??= conn.Query<Medal>("SELECT * FROM osu_achievements WHERE enabled = 1", transaction: transaction).ToImmutableArray();
        }

        private static void awardMedal(SoloScore score, Medal medal)
        {
            Console.WriteLine($"Awarding medal {medal.name} to user {score.user_id} (score {score.id})");
            WebRequestHelper.RunSharedInteropCommand($"user-achievement/{score.user_id}/{medal.achievement_id}/{score.beatmap_id}", "POST");
            MedalAwarded?.Invoke(new AwardedMedal(medal, score));
        }

        public void ApplyGlobal(SoloScore score, MySqlConnection conn)
        {
        }

        public record struct AwardedMedal(Medal Medal, SoloScore Score);

        public string DisplayString
        {
            get
            {
                var stringBuilder = new StringBuilder();

                stringBuilder.AppendLine($"- {GetType().ReadableName()} ({GetType().Assembly.FullName})");
                stringBuilder.Append("  Medal awarders:");

                foreach (var awarder in medal_awarders)
                {
                    stringBuilder.AppendLine();
                    stringBuilder.Append($"  - {awarder.GetType().ReadableName()} ({awarder.GetType().Assembly.FullName})");
                }

                return stringBuilder.ToString();
            }
        }
    }
}
