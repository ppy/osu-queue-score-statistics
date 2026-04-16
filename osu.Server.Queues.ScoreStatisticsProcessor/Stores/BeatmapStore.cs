// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Collections.Immutable;
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
using osu.Game.Rulesets.Catch.Mods;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Taiko.Mods;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using StatsdClient;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Stores
{
    /// <summary>
    /// A store for retrieving <see cref="Models.Beatmap"/>s.
    /// </summary>
    public static class BeatmapStore
    {
        public static readonly string? DIFF_ATTRIB_DATABASE = Environment.GetEnvironmentVariable("DB_NAME_DIFFICULTY") ?? Environment.GetEnvironmentVariable("DB_NAME") ?? "osu";

        private static readonly bool always_use_realtime_difficulty_calculation = Environment.GetEnvironmentVariable("ALWAYS_USE_REALTIME_DIFFICULTY") != "0";
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

        private static readonly MemoryCache attribute_memory_cache;

        private static readonly MemoryCache beatmap_memory_cache;

        private static readonly IReadOnlyDictionary<BlacklistEntry, byte> blacklist;

        private static int beatmapCacheMiss;
        private static int attribCacheMiss;

        public static void PurgeCaches()
        {
            attribute_memory_cache.Clear();
            beatmap_memory_cache.Clear();
        }

        public static string GetCacheStats()
        {
            string output = $"caches: [beatmap {beatmap_memory_cache.Count:N0} +{beatmapCacheMiss:N0}] [attrib {attribute_memory_cache.Count:N0} +{attribCacheMiss:N0}]";

            Interlocked.Exchange(ref beatmapCacheMiss, 0);
            Interlocked.Exchange(ref attribCacheMiss, 0);

            return output;
        }

        static BeatmapStore()
        {
            using (var connection = DatabaseAccess.GetConnection())
            {
                var dbBlacklist = connection.Query<PerformanceBlacklistEntry>("SELECT * FROM osu_beatmap_performance_blacklist");
                blacklist = dbBlacklist.Select(b => new KeyValuePair<BlacklistEntry, byte>(new BlacklistEntry(b.beatmap_id, b.mode), 1)).ToImmutableDictionary();
            }

            attribute_memory_cache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = memory_cache_size_limit,
            });

            beatmap_memory_cache = new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = memory_cache_size_limit,
            });

            _ = BeatmapStatusWatcher.StartPollingAsync(updates =>
            {
                foreach (int beatmapSetId in updates.BeatmapSetIDs)
                {
                    using (var db = DatabaseAccess.GetConnection())
                    {
                        var attributeCacheKeys = attribute_memory_cache.Keys.OfType<DifficultyAttributeKey>();

                        foreach (int beatmapId in db.Query<int>("SELECT beatmap_id FROM osu_beatmaps WHERE beatmapset_id = @beatmapSetId AND `deleted_at` IS NULL",
                                     new { beatmapSetId }))
                        {
                            Console.WriteLine($"Invalidating cache for beatmap_id {beatmapId}");

                            beatmap_memory_cache.Remove(beatmapId);

                            foreach (var key in attributeCacheKeys)
                            {
                                if (key.BeatmapId == beatmapId)
                                    attribute_memory_cache.Remove(key);
                            }
                        }
                    }
                }
            });
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
        public static async Task<DifficultyAttributes> GetDifficultyAttributesAsync(Beatmap beatmap, Ruleset ruleset, Mod[] mods, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            // database attributes are stored using the default mod configurations
            // if we want to support mods with non-default configurations (i.e non-1.5x rates on DT/NC)
            // or non-legacy mods which aren't populated into the database (with exception to CL)
            // then we must calculate difficulty attributes in real-time.
            bool mustUseRealtimeDifficulty = mods.Any(m => !m.UsesDefaultConfiguration || (!IsRankedLegacyMod(m) && m is not ModClassic));

            if (always_use_realtime_difficulty_calculation || mustUseRealtimeDifficulty)
            {
                var stopwatch = Stopwatch.StartNew();

                using var req = new WebRequest(string.Format(beatmap_download_path, beatmap.beatmap_id));

                req.AllowInsecureRequests = true;

                await req.PerformAsync().ConfigureAwait(false);

                if (req.ResponseStream.Length == 0)
                    throw new Exception($"Retrieved zero-length beatmap ({beatmap.beatmap_id})!");

                var workingBeatmap = new StreamedWorkingBeatmap(req.ResponseStream);
                var calculator = ruleset.CreateDifficultyCalculator(workingBeatmap);

                var attributes = calculator.Calculate(mods);

                string[] tags =
                {
                    $"ruleset:{ruleset.RulesetInfo.OnlineID}",
                    $"mods:{string.Join("", mods.Select(x => x.Acronym))}"
                };

                DogStatsd.Timer("calculate-realtime-difficulty-attributes", stopwatch.ElapsedMilliseconds, tags: tags);

                return attributes;
            }

            DifficultyAttributeKey key = new DifficultyAttributeKey(beatmap.beatmap_id, (uint)ruleset.RulesetInfo.OnlineID, (uint)getLegacyModsForAttributeLookup(beatmap, ruleset, mods));

            return (await attribute_memory_cache.GetOrCreateAsync(key, async cacheEntry =>
            {
                try
                {
                    BeatmapDifficultyAttribute[] dbAttributes = (await connection.QueryAsync<BeatmapDifficultyAttribute>(
                        $"SELECT * FROM {DIFF_ATTRIB_DATABASE}.osu_beatmap_difficulty_attribs WHERE `beatmap_id` = @BeatmapId AND `mode` = @RulesetId AND `mods` = @ModValue", new
                        {
                            key.BeatmapId,
                            key.RulesetId,
                            key.ModValue
                        }, transaction: transaction)).ToArray();

                    // approximated
                    cacheEntry.SetSize(difficulty_attribute_size + beatmap_difficulty_attribute_size * dbAttributes.Length);
                    cacheEntry.SetSlidingExpiration(memory_cache_sliding_expiration);

                    DifficultyAttributes attributes = LegacyRulesetHelper.CreateDifficultyAttributes(ruleset.RulesetInfo.OnlineID);
                    attributes.FromDatabaseAttributes(dbAttributes.ToDictionary(a => (int)a.attrib_id, a => (double)a.value), beatmap);
                    return attributes;
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
        /// This method attempts to create a simple solution to deciding if a <see cref="Mod"/> can be considered a ranked "legacy" mod.
        /// Used by <see cref="GetDifficultyAttributesAsync"/> to decide if the current mod combination's difficulty attributes
        /// can be fetched from the database.
        /// </remarks>
        public static bool IsRankedLegacyMod(Mod mod) =>
            mod is ModNoFail
                or ModEasy
                or ModPerfect
                or ModSuddenDeath
                or ModNightcore
                or ModDoubleTime
                or ModHalfTime
                or ModFlashlight
                or ModTouchDevice
                or OsuModHardRock
                or OsuModSpunOut
                or OsuModHidden
                or TaikoModHardRock
                or TaikoModHidden
                or CatchModHardRock
                or CatchModHidden
                or ManiaModKey4
                or ManiaModKey5
                or ManiaModKey6
                or ManiaModKey7
                or ManiaModKey8
                or ManiaModKey9
                or ManiaModMirror
                or ManiaModHidden
                or ManiaModFadeIn;

        /// <remarks>
        /// This method attempts to choose the best possible set of <see cref="LegacyMods"/> to use for looking up stored difficulty attributes.
        /// The match is not always exact; for some mods that award pp but do not exist in stable
        /// (such as <see cref="ModHalfTime"/>) the closest available approximation is used.
        /// Moreover, the set of <see cref="LegacyMods"/> returned is constrained to mods that actually affect difficulty in the legacy sense.
        /// The entirety of this workaround is not used / unnecessary if <see cref="always_use_realtime_difficulty_calculation"/> is <see langword="true"/>.
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
        public static Task<Beatmap?> GetBeatmapAsync(uint beatmapId, MySqlConnection connection, MySqlTransaction? transaction = null) => beatmap_memory_cache.GetOrCreateAsync(
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

        /// <summary>
        /// Whether performance points may be awarded for the given beatmap and ruleset combination.
        /// </summary>
        /// <param name="beatmap">The beatmap.</param>
        /// <param name="rulesetId">The ruleset.</param>
        public static bool IsBeatmapValidForPerformance(Beatmap beatmap, uint rulesetId)
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
