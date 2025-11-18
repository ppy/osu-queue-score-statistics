// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using MySqlConnector;
using osu.Framework.Extensions.TypeExtensions;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public interface IProcessor
    {
        /// <summary>
        /// The order in which this <see cref="IProcessor"/> will be run.
        /// Defaults to 0.
        /// </summary>
        /// <remarks>
        /// Higher values indicate this <see cref="IProcessor"/> runs after other <see cref="IProcessor"/>s with a smaller <see cref="Order"/> value.
        /// </remarks>
        int Order => 0;

        /// <summary>
        /// Whether this processor should be run on failed scores.
        /// </summary>
        bool RunOnFailedScores { get; }

        /// <summary>
        /// Whether this processor should be run on imported legacy scores.
        /// </summary>
        bool RunOnLegacyScores { get; }

        /// <summary>
        /// Reverts the effect of <paramref name="score"/> from all relevant statistics.
        /// </summary>
        /// <param name="score">The score being reverted.</param>
        /// <param name="userStats">The affected user's statistics.</param>
        /// <param name="previousVersion">The version of score statistics processor that processed this score the last time.</param>
        /// <param name="conn">Database connection to use when executing queries and commands.</param>
        /// <param name="transaction">Ongoing database transactions.</param>
        /// <param name="postTransactionActions">Queue of relevant actions to execute after the transaction ends.</param>
        void RevertFromUserStats(SoloScore score, UserStats userStats, int previousVersion, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions);

        /// <summary>
        /// Applies the effect of <paramref name="score"/> to all relevant statistics.
        /// </summary>
        /// <param name="score">The score being applied.</param>
        /// <param name="userStats">The affected user's statistics.</param>
        /// <param name="conn">Database connection to use when executing queries and commands.</param>
        /// <param name="transaction">Ongoing database transactions.</param>
        /// <param name="postTransactionActions">Queue of relevant actions to execute after the transaction ends.</param>
        void ApplyToUserStats(SoloScore score, UserStats userStats, MySqlConnection conn, MySqlTransaction transaction, List<Action> postTransactionActions);

        /// <summary>
        /// Adjust any global statistics outside of the user transaction.
        /// Anything done in here should not need to be rolled back.
        /// </summary>
        /// <param name="score">The user's score.</param>
        /// <param name="conn">The database connection.</param>
        void ApplyGlobal(SoloScore score, MySqlConnection conn);

        string DisplayString => $"- {GetType().ReadableName()} ({GetType().Assembly.FullName})";
    }
}
