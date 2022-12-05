// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// A class which handles conditional awarding of one of more medals.
    /// </summary>
    [UsedImplicitly]
    public interface IMedalAwarder
    {
        /// <summary>
        /// For a given score and collection of valid medals, check which should be awarded (if any).
        /// </summary>
        /// <param name="score">The score to be checked.</param>
        /// <param name="medals">All medals available for potential awarding.</param>
        /// <param name="conn">The MySQL connection.</param>
        /// <param name="transaction">The active transaction.</param>
        /// <returns></returns>
        IEnumerable<Medal> Check(SoloScoreInfo score, IEnumerable<Medal> medals, MySqlConnection conn, MySqlTransaction transaction);
    }
}
