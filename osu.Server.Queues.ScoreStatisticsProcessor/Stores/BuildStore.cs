// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Stores
{
    /// <summary>
    /// A store for retrieving <see cref="Build"/>s.
    /// </summary>
    public class BuildStore
    {
        private static readonly uint memory_cache_size_limit = uint.Parse(Environment.GetEnvironmentVariable("MEMORY_CACHE_SIZE_LIMIT") ?? "128000000");

        private readonly MemoryCache buildCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = memory_cache_size_limit });

        /// <summary>
        /// The size of a <see cref="Build"/> in bytes. Used for tracking memory usage.
        /// </summary>
        private const int build_size = 6;

        public Task<Build?> GetBuildAsync(uint buildId, MySqlConnection connection, MySqlTransaction? transaction = null) => buildCache.GetOrCreateAsync(
            buildId,
            cacheEntry =>
            {
                // Quite short so we can delist builds which are no longer valid for ranking.
                cacheEntry.SetAbsoluteExpiration(TimeSpan.FromSeconds(300));
                cacheEntry.SetSize(build_size);

                return connection.QuerySingleOrDefaultAsync<Build?>("SELECT build_id, allow_ranking, allow_performance FROM osu_builds WHERE build_id = @BuildId", new
                {
                    BuildId = buildId
                }, transaction: transaction);
            });
    }
}
