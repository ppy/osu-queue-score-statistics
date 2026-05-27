// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using MySqlConnector;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    /// <summary>
    /// Contains all necessary context and access method for implementing <see cref="IMedalAwarder"/>s.
    /// </summary>
    public record MedalAwarderContext
    {
        /// <summary>
        /// The score to check for medals.
        /// </summary>
        public SoloScore Score { get; }

        /// <summary>
        /// The calculated user statistics after <see cref="Score"/>.
        /// </summary>
        public UserStats UserStats { get; }

        /// <summary>
        /// The user's daily challenge stats after <see cref="Score"/>.
        /// </summary>
        public DailyChallengeUserStats DailyChallengeUserStats { get; }

        /// <summary>
        /// MySQL connection for manual retrieval from database.
        /// </summary>
        public MySqlConnection Connection { get; }

        /// <summary>
        /// MySQL transaction for manual retrieval from database.
        /// </summary>
        public MySqlTransaction Transaction { get; }

        /// <param name="score">The score to check for medals.</param>
        /// <param name="userStats">The calculated user statistics after <see cref="Score"/>.</param>
        /// <param name="dailyChallengeUserStats">The user's daily challenge stats after <see cref="Score"/>.</param>
        /// <param name="connection">MySQL connection for manual retrieval from database.</param>
        /// <param name="transaction">MySQL transaction for manual retrieval from database.</param>
        public MedalAwarderContext(
            SoloScore score,
            UserStats userStats,
            DailyChallengeUserStats dailyChallengeUserStats,
            MySqlConnection connection,
            MySqlTransaction transaction)
        {
            Score = score;
            UserStats = userStats;
            DailyChallengeUserStats = dailyChallengeUserStats;
            Connection = connection;
            Transaction = transaction;
        }

        public MedalAwarderContext(MedalAwarderContext other)
        {
            Score = other.Score;
            UserStats = other.UserStats;
            DailyChallengeUserStats = other.DailyChallengeUserStats;
            Connection = other.Connection;
            Transaction = other.Transaction;
        }

        /// <summary>
        /// Returns difficulty attributes for the supplied <paramref name="beatmap"/>, <param name="ruleset"></param>,
        /// and <paramref name="mods"/> combination.
        /// If the <paramref name="beatmap"/> is not ranked, approved, qualified, or loved, this will return <see langword="null"/>
        /// (because the attributes are not available).
        /// </summary>
        public async Task<DifficultyAttributes?> GetDifficultyAttributesAsync(Beatmap beatmap, Ruleset ruleset, Mod[] mods)
        {
            // matches https://github.com/ppy/osu-difficulty-calculator/blob/2da917fb21e752879d34820a60e76a95d90ac24f/osu.Server.DifficultyCalculator/ServerDifficultyCalculator.cs#L79-L82
            if (beatmap.approved > 0)
                return await BeatmapStore.GetDifficultyAttributesAsync(beatmap, ruleset, mods, Connection, Transaction);

            return null;
        }
    }
}
