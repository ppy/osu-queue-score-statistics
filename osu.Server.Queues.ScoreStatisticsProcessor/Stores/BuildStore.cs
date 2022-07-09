// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Stores
{
    /// <summary>
    /// A store for retrieving <see cref="Build"/>s.
    /// </summary>
    public class BuildStore
    {
        private readonly IReadOnlyDictionary<int, Build> builds;

        private BuildStore(IEnumerable<KeyValuePair<int, Build>> builds)
        {
            this.builds = new Dictionary<int, Build>(builds);
        }

        /// <summary>
        /// Creates a new <see cref="BuildStore"/>.
        /// </summary>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The created <see cref="BuildStore"/>.</returns>
        public static async Task<BuildStore> CreateAsync(MySqlConnection? connection, MySqlTransaction? transaction = null)
        {
            var dbBuilds = await connection.QueryAsync<Build>($"SELECT * FROM {Build.TABLE_NAME} WHERE `allow_ranking` = TRUE OR `allow_performance` = TRUE", transaction: transaction);

            return new BuildStore
            (
                dbBuilds.Select(b => new KeyValuePair<int, Build>(b.build_id, b))
            );
        }

        /// <summary>
        /// Retrieves a build from the database.
        /// </summary>
        /// <param name="buildId">The build's id.</param>
        /// <returns>The retrieved build, or <c>null</c> if not existing.</returns>
        public Build? GetBuild(int buildId) => builds.GetValueOrDefault(buildId);
    }
}
