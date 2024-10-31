// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using MySqlConnector;
using osu.Framework.IO.Network;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Stores
{
    /// <summary>
    /// A store for retrieving <see cref="Models.Beatmap"/>s.
    /// </summary>
    public class BeatmapStore
    {
        private static readonly bool use_realtime_difficulty_calculation = Environment.GetEnvironmentVariable("REALTIME_DIFFICULTY") != "0";
        private static readonly string beatmap_download_path = Environment.GetEnvironmentVariable("BEATMAP_DOWNLOAD_PATH") ?? "https://osu.ppy.sh/osu/{0}";
        private static readonly uint memory_cache_size_limit = uint.Parse(Environment.GetEnvironmentVariable("MEMORY_CACHE_SIZE_LIMIT") ?? "128000000");
        private static readonly TimeSpan memory_cache_sliding_expiration = TimeSpan.FromSeconds(uint.Parse(Environment.GetEnvironmentVariable("MEMORY_CACHE_SLIDING_EXPIRATION_SECONDS") ?? "3600"));

        /// <summary>
        /// The size of a <see cref="BeatmapDifficultyAttribute"/> in bytes. Used for tracking memory usage.
        /// </summary>
        private const int beatmap_difficulty_attribute_size = 24;

        /// <summary>
        /// The rough size of <see cref="DifficultyAttributes"/> base class in bytes.
        /// </summary>
        private const int difficulty_attribute_size = 24;

        /// <summary>
        /// The size of a <see cref="Beatmap"/> in bytes. Used for tracking memory usage.
        /// </summary>
        private const int beatmap_size = 72;

        private readonly MemoryCache attributeMemoryCache;

        private readonly MemoryCache beatmapMemoryCache;
        private readonly IReadOnlyDictionary<BlacklistEntry, byte> blacklist;

        private int beatmapCacheMiss;
        private int attribCacheMiss;

        public string GetCacheStats()
        {
            string output = $"caches: [beatmap {beatmapMemoryCache.Count:N0} +{beatmapCacheMiss:N0}] [attrib {attributeMemoryCache.Count:N0} +{attribCacheMiss:N0}]";

            Interlocked.Exchange(ref beatmapCacheMiss, 0);
            Interlocked.Exchange(ref attribCacheMiss, 0);

            return output;
        }

        private BeatmapStore(IEnumerable<KeyValuePair<BlacklistEntry, byte>> blacklist)
        {
            this.blacklist = new Dictionary<BlacklistEntry, byte>(blacklist);

            attributeMemoryCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = memory_cache_size_limit,
            });

            beatmapMemoryCache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = memory_cache_size_limit,
            });
        }

        /// <summary>
        /// Creates a new <see cref="BeatmapStore"/>.
        /// </summary>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The created <see cref="BeatmapStore"/>.</returns>
        public static async Task<BeatmapStore> CreateAsync(MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            var dbBlacklist = await connection.QueryAsync<PerformanceBlacklistEntry>("SELECT * FROM osu_beatmap_performance_blacklist", transaction: transaction);

            return new BeatmapStore
            (
                dbBlacklist.Select(b => new KeyValuePair<BlacklistEntry, byte>(new BlacklistEntry(b.beatmap_id, b.mode), 1))
            );
        }

        /// <summary>
        /// Retrieves difficulty attributes from the database.
        /// </summary>
        /// <param name="beatmap">The beatmap.</param>
        /// <param name="ruleset">The score's ruleset.</param>
        /// <param name="mods">The score's mods.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The difficulty attributes or <c>null</c> if not existing.</returns>
        /// <exception cref="DifficultyAttributesMissingException">If the difficulty attributes don't exist in the database.</exception>
        /// <exception cref="Exception">If realtime difficulty attributes couldn't be computed.</exception>
        public async Task<DifficultyAttributes> GetDifficultyAttributesAsync(Beatmap beatmap, Ruleset ruleset, Mod[] mods, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            if (use_realtime_difficulty_calculation)
            {
                using var req = new WebRequest(string.Format(beatmap_download_path, beatmap.beatmap_id));

                req.AllowInsecureRequests = true;

                await req.PerformAsync().ConfigureAwait(false);

                if (req.ResponseStream.Length == 0)
                    throw new Exception($"Retrieved zero-length beatmap ({beatmap.beatmap_id})!");

                var workingBeatmap = new StreamedWorkingBeatmap(req.ResponseStream);
                var calculator = ruleset.CreateDifficultyCalculator(workingBeatmap);

                return calculator.Calculate(mods);
            }

            LegacyMods legacyModValue = getLegacyModsForAttributeLookup(beatmap, ruleset, mods);

            DifficultyAttributeKey key = new DifficultyAttributeKey(beatmap.beatmap_id, (uint)ruleset.RulesetInfo.OnlineID, (uint)legacyModValue);

            return (await attributeMemoryCache.GetOrCreateAsync(
                key,
                async cacheEntry =>
                {
                    try
                    {
                        DifficultyAttributes attribs = LegacyRulesetHelper.CreateDifficultyAttributes(ruleset.RulesetInfo.OnlineID);

                        BeatmapDifficultyAttribute[] dbAttribs = (await connection.QueryAsync<BeatmapDifficultyAttribute>(
                            "SELECT * FROM osu_beatmap_difficulty_attribs WHERE `beatmap_id` = @BeatmapId AND `mode` = @RulesetId AND `mods` = @ModValue", new
                            {
                                key.BeatmapId,
                                key.RulesetId,
                                key.ModValue
                            }, transaction: transaction)).ToArray();

                        attribs.FromDatabaseAttributes(dbAttribs.ToDictionary(a => (int)a.attrib_id, a => (double)a.value), beatmap);

                        cacheEntry.SetSlidingExpiration(memory_cache_sliding_expiration);

                        // approximated
                        cacheEntry.SetSize(difficulty_attribute_size + beatmap_difficulty_attribute_size * dbAttribs.Length);

                        return attribs;
                    }
                    catch (Exception ex)
                    {
                        throw new DifficultyAttributesMissingException(key, ex);
                    }
                    finally
                    {
                        Interlocked.Increment(ref attribCacheMiss);
                    }
                }))!;
        }

        /// <remarks>
        /// This method attempts to choose the best possible set of <see cref="LegacyMods"/> to use for looking up stored difficulty attributes.
        /// The match is not always exact; for some mods that award pp but do not exist in stable
        /// (such as <see cref="ModHalfTime"/>) the closest available approximation is used.
        /// Moreover, the set of <see cref="LegacyMods"/> returned is constrained to mods that actually affect difficulty in the legacy sense.
        /// The entirety of this workaround is not used / unnecessary if <see cref="use_realtime_difficulty_calculation"/> is <see langword="true"/>.
        /// </remarks>
        private static LegacyMods getLegacyModsForAttributeLookup(Beatmap beatmap, Ruleset ruleset, Mod[] mods)
        {
            var legacyMods = ruleset.ConvertToLegacyMods(mods);

            // mods that are not represented in `LegacyMods` (but we can approximate them well enough with others)
            if (mods.Any(mod => mod is ModDaycore))
                legacyMods |= LegacyMods.HalfTime;

            return LegacyModsHelper.MaskRelevantMods(legacyMods, ruleset.RulesetInfo.OnlineID != beatmap.playmode, ruleset.RulesetInfo.OnlineID);
        }

        /// <summary>
        /// Retrieves a beatmap from the database.
        /// </summary>
        /// <param name="beatmapId">The beatmap's ID.</param>
        /// <param name="connection">The <see cref="MySqlConnection"/>.</param>
        /// <param name="transaction">An existing transaction.</param>
        /// <returns>The retrieved beatmap, or <c>null</c> if not existing.</returns>
        public Task<Beatmap?> GetBeatmapAsync(uint beatmapId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            return beatmapMemoryCache.GetOrCreateAsync(
                beatmapId,
                cacheEntry =>
                {
                    Interlocked.Increment(ref beatmapCacheMiss);

                    cacheEntry.SetSlidingExpiration(memory_cache_sliding_expiration);
                    cacheEntry.SetSize(beatmap_size);

                    return connection.QuerySingleOrDefaultAsync<Beatmap?>("SELECT * FROM osu_beatmaps WHERE `beatmap_id` = @BeatmapId", new
                    {
                        BeatmapId = beatmapId
                    }, transaction: transaction);
                });
        }

        /// <summary>
        /// Whether performance points may be awarded for the given beatmap and ruleset combination.
        /// </summary>
        /// <param name="beatmap">The beatmap.</param>
        /// <param name="rulesetId">The ruleset.</param>
        public bool IsBeatmapValidForPerformance(Beatmap beatmap, uint rulesetId)
        {
            if (blacklist.ContainsKey(new BlacklistEntry(beatmap.beatmap_id, rulesetId)))
                return false;

            switch (beatmap.approved)
            {
                case BeatmapOnlineStatus.Ranked:
                case BeatmapOnlineStatus.Approved:
                    return true;

                default:
                    return false;
            }
        }

        public record struct DifficultyAttributeKey(uint BeatmapId, uint RulesetId, uint ModValue);

        [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Local")]
        private record struct BlacklistEntry(uint BeatmapId, uint RulesetId);
    }

    public class DifficultyAttributesMissingException : Exception
    {
        public DifficultyAttributesMissingException(BeatmapStore.DifficultyAttributeKey key, Exception? inner)
            : base(key.ToString(), inner)
        {
        }
    }
}
