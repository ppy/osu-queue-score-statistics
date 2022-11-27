// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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
    internal interface IMedalAwarder
    {
        bool Check(SoloScoreInfo info, Medal medal, MySqlConnection conn);
    }
}
