// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
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
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using StatsdClient;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Stores
{
    /// <summary>
    /// A store for retrieving <see cref="Models.Beatmap"/>s.
    /// </summary>
    public class BeatmapStore
    {
        private static readonly bool prefer_realtime_difficulty_calculation = Environment.GetEnvironmentVariable("PREFER_REALTIME_DIFFICULTY") != "0";
        private static readonly string beatmap_download_path = Environment.GetEnvironmentVariable("BEATMAP_DOWNLOAD_PATH") ?? "https://osu.ppy.sh/osu/{0}";

        private readonly ConcurrentDictionary<uint, Beatmap?> beatmapCache = new ConcurrentDictionary<uint, Beatmap?>();
        private readonly ConcurrentDictionary<DifficultyAttributeKey, BeatmapDifficultyAttribute[]?> attributeCache = new ConcurrentDictionary<DifficultyAttributeKey, BeatmapDifficultyAttribute[]?>();
        private readonly IReadOnlyDictionary<BlacklistEntry, byte> blacklist;

        private BeatmapStore(IEnumerable<KeyValuePair<BlacklistEntry, byte>> blacklist)
        {
            this.blacklist = new Dictionary<BlacklistEntry, byte>(blacklist);
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
        public async Task<DifficultyAttributes?> GetDifficultyAttributesAsync(Beatmap beatmap, Ruleset ruleset, Mod[] mods, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            // database attributes are stored using the default mod configurations
            // if we want to support mods with non-default configurations (i.e non-1.5x rates on DT/NC)
            // or non-legacy mods which aren't populated into the database (with exception to CL)
            // then we must calculate difficulty attributes in real-time.
            bool mustUseRealtimeDifficulty = mods.Any(m => !m.UsesDefaultConfiguration || (!isRankedLegacyMod(m) && m is not ModClassic));

            if (prefer_realtime_difficulty_calculation || mustUseRealtimeDifficulty)
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

            BeatmapDifficultyAttribute[]? rawDifficultyAttributes;

            LegacyMods legacyModValue = getLegacyModsForAttributeLookup(beatmap, ruleset, mods);
            DifficultyAttributeKey key = new DifficultyAttributeKey(beatmap.beatmap_id, (uint)ruleset.RulesetInfo.OnlineID, (uint)legacyModValue);

            if (!attributeCache.TryGetValue(key, out rawDifficultyAttributes))
            {
                rawDifficultyAttributes = attributeCache[key] = (await connection.QueryAsync<BeatmapDifficultyAttribute>(
                    "SELECT * FROM osu_beatmap_difficulty_attribs WHERE `beatmap_id` = @BeatmapId AND `mode` = @RulesetId AND `mods` = @ModValue", new
                    {
                        key.BeatmapId,
                        key.RulesetId,
                        key.ModValue
                    }, transaction: transaction)).ToArray();
            }

            if (rawDifficultyAttributes == null || rawDifficultyAttributes.Length == 0)
                return null;

            DifficultyAttributes difficultyAttributes = LegacyRulesetHelper.CreateDifficultyAttributes(ruleset.RulesetInfo.OnlineID);
            difficultyAttributes.FromDatabaseAttributes(rawDifficultyAttributes.ToDictionary(a => (int)a.attrib_id, a => (double)a.value), beatmap);

            return difficultyAttributes;
        }

        /// <remarks>
        /// This method attempts to create a simple solution to deciding if a <see cref="Mod"/> can be considered a ranked "legacy" mod.
        /// Used by <see cref="GetDifficultyAttributesAsync"/> to decide if the current mod combination's difficulty attributes
        /// can be fetched from the database.
        /// </remarks>
        private static bool isRankedLegacyMod(Mod mod) =>
            mod is ModNoFail
                or ModEasy
                or ModHidden // this also catches ManiaModFadeIn
                or ModPerfect
                or ModSuddenDeath
                or ModNightcore
                or ModDoubleTime
                or ModHalfTime
                or ModFlashlight
                or ModTouchDevice
                or OsuModHardRock
                or OsuModSpunOut
                or TaikoModHardRock
                or CatchModHardRock
                or ManiaModKey4
                or ManiaModKey5
                or ManiaModKey6
                or ManiaModKey7
                or ManiaModKey8
                or ManiaModKey9
                or ManiaModMirror;

        /// <remarks>
        /// This method attempts to choose the best possible set of <see cref="LegacyMods"/> to use for looking up stored difficulty attributes.
        /// The match is not always exact; for some mods that award pp but do not exist in stable
        /// (such as <see cref="ModHalfTime"/>) the closest available approximation is used.
        /// Moreover, the set of <see cref="LegacyMods"/> returned is constrained to mods that actually affect difficulty in the legacy sense.
        /// The entirety of this workaround is not used / unnecessary if <see cref="prefer_realtime_difficulty_calculation"/> is <see langword="true"/>.
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
        public async Task<Beatmap?> GetBeatmapAsync(uint beatmapId, MySqlConnection connection, MySqlTransaction? transaction = null)
        {
            if (beatmapCache.TryGetValue(beatmapId, out var beatmap))
                return beatmap;

            return beatmapCache[beatmapId] = await connection.QuerySingleOrDefaultAsync<Beatmap?>("SELECT * FROM osu_beatmaps WHERE `beatmap_id` = @BeatmapId", new
            {
                BeatmapId = beatmapId
            }, transaction: transaction);
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

        private record struct DifficultyAttributeKey(uint BeatmapId, uint RulesetId, uint ModValue);

        [SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Local")]
        private record struct BlacklistEntry(uint BeatmapId, uint RulesetId);
    }
}
