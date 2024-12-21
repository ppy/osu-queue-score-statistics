// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    /// <summary>
    /// Contains all necessary context and access method for implementing <see cref="IMedalAwarder"/>s.
    /// </summary>
    /// <param name="Score">The score to check for medals.</param>
    /// <param name="UserStats">The calculated user statistics after <see cref="Score"/>.</param>
    /// <param name="DailyChallengeUserStats">The user's daily challenge stats after <see cref="Score"/>.</param>
    /// <param name="BeatmapStore">Allows retrieval of <see cref="Beatmap"/>s from database.</param>
    /// <param name="Connection">MySQL connection for manual retrieval from database.</param>
    /// <param name="Transaction">MySQL transaction for manual retrieval from database.</param>
    public record MedalAwarderContext(
        SoloScore Score,
        UserStats UserStats,
        DailyChallengeUserStats DailyChallengeUserStats,
        BeatmapStore BeatmapStore,
        MySqlConnection Connection,
        MySqlTransaction Transaction);
}
