// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dapper;
using Dapper.Contrib.Extensions;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Rulesets.Mania.Difficulty;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Taiko.Difficulty;
using osu.Game.Utils;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;
using Xunit;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class MedalProcessorTests : DatabaseTest
    {
        private readonly List<MedalProcessor.AwardedMedal> awardedMedals = new List<MedalProcessor.AwardedMedal>();
        private static int lastBeatmapId = 2;

        public MedalProcessorTests()
        {
            MedalProcessor.MedalAwarded += onMedalAwarded;

            using (var db = Processor.GetDatabaseConnection())
            {
                db.Execute("TRUNCATE TABLE osu_achievements");
                db.Execute("TRUNCATE TABLE osu_user_achievements");

                db.Execute("TRUNCATE TABLE osu_beatmappacks");
                db.Execute("TRUNCATE TABLE osu_beatmappacks_items");
            }
        }

        /// <summary>
        /// BeatmapStore (used in StarRatingMedalAwarder) may cache beatmap and difficulty information,
        /// in order to avoid using stale data, we use unique incrementing beatmap IDs.
        /// </summary>
        private static uint getNextBeatmapId() => (uint)Interlocked.Increment(ref lastBeatmapId);

        public static readonly object[][] MEDAL_PACK_IDS =
        {
            [7, 40], // Normal pack
            [267, 2036], // Challenge pack
        };

        /// <summary>
        /// The medal processor should skip medals which have already been awarded.
        /// There are no medals which should trigger more than once.
        /// </summary>
        [Theory]
        [MemberData(nameof(MEDAL_PACK_IDS))]
        public void TestOnlyAwardsOnce(int medalId, int packId)
        {
            var beatmap = AddBeatmap();

            addPackMedal(medalId, packId, new[] { beatmap });

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.preserve = true);

            assertAwarded(medalId);

            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.preserve = true);
            assertAwarded(medalId);
        }

        /// <summary>
        /// The pack awarder should skip scores that are failed.
        /// </summary>
        [Theory]
        [MemberData(nameof(MEDAL_PACK_IDS))]
        public void TestDoesNotAwardOnFailedScores(int medalId, int packId)
        {
            var beatmap = AddBeatmap();

            addPackMedal(medalId, packId, new[] { beatmap });

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.passed = false);

            assertNoneAwarded();
        }

        /// <summary>
        /// The pack awarder should skip scores that are failed.
        /// </summary>
        [Theory]
        [MemberData(nameof(MEDAL_PACK_IDS))]
        public void TestPackMedalsDoNotIncludePreviousFails(int medalId, int packId)
        {
            var firstBeatmap = AddBeatmap(b => b.beatmap_id = 1234, s => s.beatmapset_id = 4321);
            var secondBeatmap = AddBeatmap(b => b.beatmap_id = 5678, s => s.beatmapset_id = 8765);

            addPackMedal(medalId, packId, new[] { firstBeatmap, secondBeatmap });

            assertNoneAwarded();

            SetScoreForBeatmap(firstBeatmap.beatmap_id, s =>
            {
                s.Score.passed = false;
                s.Score.preserve = false;
            });
            assertNoneAwarded();

            SetScoreForBeatmap(secondBeatmap.beatmap_id, s =>
            {
                s.Score.passed = true;
                s.Score.preserve = true;
            });
            assertNoneAwarded();
        }

        /// <summary>
        /// This tests the simplest case of a medal being awarded for completing a pack.
        /// This mimics the "video game" pack, but is intended to test the process rather than the
        /// content of that pack specifically.
        /// </summary>
        [Theory]
        [MemberData(nameof(MEDAL_PACK_IDS))]
        public void TestSimplePack(int medalId, int packId)
        {
            var allBeatmaps = new[]
            {
                AddBeatmap(b => b.beatmap_id = 71621, s => s.beatmapset_id = 13022),
                AddBeatmap(b => b.beatmap_id = 59225, s => s.beatmapset_id = 16520),
                AddBeatmap(b => b.beatmap_id = 79288, s => s.beatmapset_id = 23073),
                AddBeatmap(b => b.beatmap_id = 101236, s => s.beatmapset_id = 27936),
                AddBeatmap(b => b.beatmap_id = 105325, s => s.beatmapset_id = 32162),
                AddBeatmap(b => b.beatmap_id = 127762, s => s.beatmapset_id = 40233),
                AddBeatmap(b => b.beatmap_id = 132751, s => s.beatmapset_id = 42158),
                AddBeatmap(b => b.beatmap_id = 134948, s => s.beatmapset_id = 42956),
                AddBeatmap(b => b.beatmap_id = 177972, s => s.beatmapset_id = 59370),
                AddBeatmap(b => b.beatmap_id = 204837, s => s.beatmapset_id = 71476),
                AddBeatmap(b => b.beatmap_id = 206298, s => s.beatmapset_id = 72137),
                AddBeatmap(b => b.beatmap_id = 271917, s => s.beatmapset_id = 102913),
                AddBeatmap(b => b.beatmap_id = 514849, s => s.beatmapset_id = 169848),
                AddBeatmap(b => b.beatmap_id = 497769, s => s.beatmapset_id = 211704),
            };

            addPackMedal(medalId, packId, allBeatmaps);

            // Need to space out submissions else we will hit rate limits.
            int minutesOffset = -allBeatmaps.Length;

            foreach (var beatmap in allBeatmaps)
            {
                assertNoneAwarded();
                SetScoreForBeatmap(beatmap.beatmap_id, s =>
                {
                    s.Score.ended_at = DateTimeOffset.Now.AddMinutes(minutesOffset++);
                    s.Score.preserve = true;
                });
            }

            assertAwarded(medalId);

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", allBeatmaps.Length, CancellationToken);
        }

        /// <summary>
        /// Beatmap packs are defined as a list of beatmap *set* IDs.
        /// When checking whether we should award, there's a need group user's plays across a single set to avoid counting
        /// plays on different difficulties of the same beatmap twice.
        /// </summary>
        [Theory]
        [MemberData(nameof(MEDAL_PACK_IDS))]
        public void TestPlayMultipleBeatmapsFromSameSetDoesNotAward(int medalId, int packId)
        {
            const int beatmapset_id = 13022;

            List<Beatmap> beatmaps = new List<Beatmap>
            {
                // Three beatmap difficulties in the same set.
                // Added to database below due to shared set requiring special handling.
                new Beatmap { approved = BeatmapOnlineStatus.Ranked, beatmap_id = 71621, beatmapset_id = beatmapset_id },
                new Beatmap { approved = BeatmapOnlineStatus.Ranked, beatmap_id = 71622, beatmapset_id = beatmapset_id },
                new Beatmap { approved = BeatmapOnlineStatus.Ranked, beatmap_id = 71623, beatmapset_id = beatmapset_id }
            };

            using (var db = Processor.GetDatabaseConnection())
            {
                db.Insert(new BeatmapSet { approved = BeatmapOnlineStatus.Ranked, beatmapset_id = beatmapset_id });
                foreach (var beatmap in beatmaps)
                    db.Insert(beatmap);
            }

            // A final beatmap in a different set.
            beatmaps.Add(AddBeatmap(b => b.beatmap_id = 59225, s => s.beatmapset_id = 16520));

            Assert.Equal(4, beatmaps.Count);

            addPackMedal(medalId, packId, beatmaps);

            foreach (var beatmap in beatmaps)
            {
                assertNoneAwarded();
                SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.preserve = true);
            }

            // Awarding should only happen after the final set is hit.
            assertAwarded(medalId);
        }

        /// <summary>
        /// We may have multiple scores in the database for a single user-beatmap combo.
        /// Only one should be counted.
        /// </summary>
        [Theory]
        [MemberData(nameof(MEDAL_PACK_IDS))]
        public void TestPlayMultipleTimeOnSameSetDoesNotAward(int medalId, int packId)
        {
            var beatmap1 = AddBeatmap(b => b.beatmap_id = 71623, s => s.beatmapset_id = 13022);
            var beatmap2 = AddBeatmap(b => b.beatmap_id = 59225, s => s.beatmapset_id = 16520);

            addPackMedal(medalId, packId, new[] { beatmap1, beatmap2 });

            SetScoreForBeatmap(beatmap1.beatmap_id, s => s.Score.preserve = true);
            assertNoneAwarded();
            SetScoreForBeatmap(beatmap1.beatmap_id, s => s.Score.preserve = true);
            assertNoneAwarded();
            SetScoreForBeatmap(beatmap1.beatmap_id, s => s.Score.preserve = true);
            assertNoneAwarded();

            SetScoreForBeatmap(beatmap2.beatmap_id, s => s.Score.preserve = true);
            assertAwarded(medalId);
        }

        /// <summary>
        /// Some beatmap packs (ie. "challenge" packs) require completion without using difficulty reduction mods.
        /// This is specified as a flat in the medal's conditional.
        ///
        /// Using pack 267 as an example, this test ensures that plays with reduction mods are not counted towards completion.
        /// </summary>
        [Fact]
        public void TestNoReductionModsPack()
        {
            const int medal_id = 267;
            const int pack_id = 2036;

            var allBeatmaps = new[]
            {
                AddBeatmap(b => b.beatmap_id = 2018512, s => s.beatmapset_id = 964134),
                AddBeatmap(b => b.beatmap_id = 2051817, s => s.beatmapset_id = 980459),
                AddBeatmap(b => b.beatmap_id = 2111505, s => s.beatmapset_id = 1008679),
                AddBeatmap(b => b.beatmap_id = 2236260, s => s.beatmapset_id = 1068163),
                AddBeatmap(b => b.beatmap_id = 2285281, s => s.beatmapset_id = 1093385),
                AddBeatmap(b => b.beatmap_id = 2324126, s => s.beatmapset_id = 1112424),
            };

            addPackMedal(medal_id, pack_id, allBeatmaps);
            assertNoneAwarded();

            // Set passes without mods on all but the first map
            foreach (var beatmap in allBeatmaps.Skip(1))
            {
                SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.preserve = true);
                assertNoneAwarded();
            }

            // Pass the first map with Easy mod (difficulty reduction)
            SetScoreForBeatmap(allBeatmaps[0].beatmap_id, s =>
            {
                s.Score.ScoreData.Mods = new[] { new APIMod(new OsuModEasy()) };
                s.Score.preserve = true;
            });
            assertNoneAwarded();

            // Pass the first map without mods
            SetScoreForBeatmap(allBeatmaps[0].beatmap_id, s => s.Score.preserve = true);
            assertAwarded(medal_id);

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", allBeatmaps.Length + 1, CancellationToken);
        }

        public static readonly object[][] NO_REDUCTION_MODS_COMBINATIONS =
        {
            // No mods
            [true, new APIMod[] { }],

            // Difficulty increase
            [true, new[] { new APIMod(new OsuModDoubleTime()) }],

            // Difficulty reduction or automation
            [false, new[] { new APIMod(new OsuModNoFail()) }],
            [false, new[] { new APIMod(new OsuModRelax()) }],

            // Mixed
            [false, new[] { new APIMod(new OsuModDoubleTime()), new APIMod(new OsuModEasy()) }],
            [false, new[] { new APIMod(new OsuModDoubleTime()), new APIMod(new OsuModAutopilot()) }],
            [false, new[] { new APIMod(new OsuModDoubleTime()), new APIMod(new OsuModClassic()) }],
            [true, new[] { new APIMod(new OsuModDoubleTime()), new APIMod(new OsuModTouchDevice()) }],

            // Allowed base mod, but disallowed settings
            [false, new[] { new APIMod(new OsuModDoubleTime { SpeedChange = { Value = 1.3 } }) }],
        };

        /// <summary>
        /// Tests whether challenge pack medals are properly awarded with various mod combinations.
        /// </summary>
        [Theory]
        [MemberData(nameof(NO_REDUCTION_MODS_COMBINATIONS))]
        public void TestNoReductionModsPackWithSelectedMods(bool expectAllowed, APIMod[] mods)
        {
            const int medal_id = 267;
            const int pack_id = 2036;

            var beatmap = AddBeatmap();

            // for optimisation reasons challenge packs depend on PP awarding.
            // if a score has no PP awarded, it is presumed that it uses unranked mods, and as such is not considered for challenge packs.
            // however, to make sure that ranked mods can give PP, difficulty attributes must be present in the database.
            // therefore, add difficulty attributes for all mod combinations that give PP on stable to approximate this.
            var workingBeatmap = new FlatWorkingBeatmap(new Game.Beatmaps.Beatmap());
            var combinations = new OsuRuleset().CreateDifficultyCalculator(workingBeatmap).CreateDifficultyAdjustmentModCombinations();

            foreach (var combination in combinations)
            {
                AddBeatmapAttributes<OsuDifficultyAttributes>(setup: attr =>
                {
                    attr.Mods = ModUtils.FlattenMod(combination).ToArray();
                    attr.AimDifficulty = 3;
                    attr.SpeedDifficulty = 3;
                    attr.OverallDifficulty = 3;
                });
            }

            addPackMedal(medal_id, pack_id, new[] { beatmap });
            assertNoneAwarded();

            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ScoreData.Mods = mods;
                s.Score.preserve = true;
                s.Score.build_id = TestBuildID;
            });

            if (expectAllowed)
                assertAwarded(medal_id);
            else
                assertNoneAwarded();
        }

        /// <summary>
        /// This tests whether Mod Introduction medals are properly awarded.
        /// It also tests the fact that these medals should not be awarded, in case there are other mods enabled.
        /// </summary>
        [Fact]
        public void TestModIntroduction()
        {
            const int medal_id = 122;

            var beatmap = AddBeatmap();

            addMedal(medal_id);

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = new[] { new APIMod(new OsuModDoubleTime()), new APIMod(new OsuModEasy()) });

            // After completing the beatmaps with double time combined with easy, the medal is not awarded.
            assertNoneAwarded();

            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = new[] { new APIMod(new OsuModDoubleTime()) });

            // Once the beatmap is completed with only double time, the medal should be awarded.
            assertAwarded(medal_id);
        }

        public static readonly object[][] MOD_INTRODUCTION_COMBINATIONS =
        {
            [new[] { new APIMod(new OsuModDoubleTime()), new APIMod(new OsuModClassic()) }],
            [new[] { new APIMod(new OsuModDoubleTime()), new APIMod(new OsuModTouchDevice()) }]
        };

        /// <summary>
        /// This tests whether Mod Introduction medals are properly awarded in combination with selected mods.
        /// </summary>
        [Theory]
        [MemberData(nameof(MOD_INTRODUCTION_COMBINATIONS))]
        public void TestModIntroductionAllowedWithSelectedMods(APIMod[] mods)
        {
            const int medal_id = 122;

            var beatmap = AddBeatmap();

            addMedal(medal_id);

            assertNoneAwarded();

            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = mods);

            // When the beatmap is completed with double time and classic, the medal should be awarded.
            assertAwarded(medal_id);
        }

        /// <summary>
        /// This tests the star rating medals, both pass and FC.
        /// </summary>
        [Theory]
        [InlineData(BeatmapOnlineStatus.Ranked)]
        [InlineData(BeatmapOnlineStatus.Approved)]
        public void TestStarRatingMedals(BeatmapOnlineStatus onlineStatus)
        {
            const int medal_id_pass = 59;
            const int medal_id_fc = 67;

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = getNextBeatmapId();
                b.approved = onlineStatus;
            });
            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id);

            addMedal(medal_id_pass);
            addMedal(medal_id_fc);

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ScoreData.Statistics = new Dictionary<HitResult, int>
                {
                    { HitResult.Perfect, 3 },
                    { HitResult.Miss, 2 },
                    { HitResult.LargeBonus, 0 }
                };
                s.Score.ScoreData.MaximumStatistics = new Dictionary<HitResult, int>
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 2 }
                };
                s.Score.max_combo = 3;
            });

            // After passing the beatmaps without an FC, the pass medal is awarded, but the FC one is not.
            assertAwarded(medal_id_pass);
            assertNotAwarded(medal_id_fc);

            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ScoreData.Statistics = new Dictionary<HitResult, int>
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 2 }
                };
                s.Score.ScoreData.MaximumStatistics = new Dictionary<HitResult, int>
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 2 }
                };
                s.Score.max_combo = 5;
            });

            // Once the beatmap is FC'd, both medals should be awarded.
            assertAwarded(medal_id_pass);
            assertAwarded(medal_id_fc);
        }

        /// <summary>
        /// When a map is FC'd, both the pass and the FC medal for the given star rating should be awarded.
        /// </summary>
        [Fact]
        public void TestBothStarRatingMedalsOnFC()
        {
            const int medal_id_pass = 59;
            const int medal_id_fc = 67;

            var beatmap = AddBeatmap(b => b.beatmap_id = getNextBeatmapId());
            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id);

            addMedal(medal_id_pass);
            addMedal(medal_id_fc);

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ScoreData.Statistics = new Dictionary<HitResult, int>
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 2 }
                };
                s.Score.ScoreData.MaximumStatistics = new Dictionary<HitResult, int>
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 2 }
                };
                s.Score.max_combo = 5;
                s.Score.ruleset_id = 0;
            });

            // Once the beatmap is FC'd, both medals should be awarded.
            assertAwarded(medal_id_pass);
            assertAwarded(medal_id_fc);
        }

        /// <summary>
        /// This tests the star rating medals, to make sure a higher level pass doesn't trigger a lower-level medal.
        /// </summary>
        [Fact]
        public void TestStarRatingMedalDoesntGiveLower()
        {
            const int medal_id_5_star = 59;
            const int medal_id_4_star = 58;

            var beatmap = AddBeatmap(b => b.beatmap_id = getNextBeatmapId());
            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id);

            addMedal(medal_id_5_star);
            addMedal(medal_id_4_star);

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id);

            // After passing the 5 star beatmap, the 5 star medal is awarded, while the 4 star one is not.
            assertAwarded(medal_id_5_star);
            assertNotAwarded(medal_id_4_star);
        }

        public static readonly object[][] STAR_RATING_MEDAL_DISALLOWED_MOD_COMBINATIONS =
        {
            [new[] { new APIMod(new OsuModEasy()) }],
            [new[] { new APIMod(new OsuModSpunOut()) }],
            [new[] { new APIMod(new OsuModRelax()), new APIMod(new OsuModHardRock()) }],
            [new[] { new APIMod(new OsuModDifficultyAdjust { ApproachRate = { Value = 2 } }) }]
        };

        /// <summary>
        /// Unranked mods should not grant star rating medals, because we are currently not able to calculate accurate star rating for unranked mods.
        /// Difficulty reduction mods also should not grant star rating medals, regardless of ranked status.
        /// </summary>
        [Theory]
        [MemberData(nameof(STAR_RATING_MEDAL_DISALLOWED_MOD_COMBINATIONS))]
        public void TestStarRatingMedalsNotAwardedWhenDifficultyReductionOrUnrankedModsAreActive(APIMod[] mods)
        {
            var beatmap = AddBeatmap(b => b.beatmap_id = getNextBeatmapId());
            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id);

            int[] passMedalIds = { 55, 56, 57, 58, 59, 60, 61, 62, 242, 244 };
            int[] fcMedalIds = { 63, 64, 65, 66, 67, 68, 69, 70, 243, 245 };

            foreach (int passMedal in passMedalIds)
                addMedal(passMedal);
            foreach (int fcMedal in fcMedalIds)
                addMedal(fcMedal);

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ScoreData.Statistics = new Dictionary<HitResult, int>
                {
                    { HitResult.Perfect, 3 },
                    { HitResult.Miss, 2 },
                    { HitResult.LargeBonus, 0 }
                };
                s.Score.ScoreData.MaximumStatistics = new Dictionary<HitResult, int>
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 2 }
                };
                s.Score.ScoreData.Mods = mods;
                s.Score.max_combo = 3;
            });

            assertNoneAwarded();
        }

        /// <summary>
        /// This tests the taiko star rating medals, to make sure a special exception beatmap doesn't trigger it.
        /// </summary>
        [Fact]
        public void TestStarRatingMedalTaikoException()
        {
            const int medal_id_5_star = 75;

            // Taiko medals have an exception for https://osu.ppy.sh/beatmapsets/2626#taiko/19990
            var beatmap = AddBeatmap(b => b.beatmap_id = 19990);
            AddBeatmapAttributes<TaikoDifficultyAttributes>(beatmap.beatmap_id, mode: 1);

            addMedal(medal_id_5_star);

            assertNoneAwarded();

            // Set a score on the exception taiko beatmap
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ruleset_id = 1);

            assertNoneAwarded();
        }

        /// <summary>
        /// This tests the mania star rating medals, to make sure mod exceptions are working.
        /// </summary>
        [Fact]
        public void TestStarRatingMedalManiaModExceptions()
        {
            const int medal_id_5_star = 91;

            var beatmap = AddBeatmap(b => b.beatmap_id = getNextBeatmapId());
            AddBeatmapAttributes<ManiaDifficultyAttributes>(beatmap.beatmap_id, mode: 3);

            addMedal(medal_id_5_star);

            assertNoneAwarded();

            // Set a score with the Dual Stages mod
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ruleset_id = 3;
                s.Score.ScoreData.Mods = new[] { new APIMod(new ManiaModDualStages()) };
            });

            // This shouldn't award a medal
            assertNoneAwarded();

            // Set a score with the a modified key count, which also shouldn't award the medal
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ruleset_id = 3;
                s.Score.ScoreData.Mods = new[] { new APIMod(new ManiaModKey7()) };
            });

            assertNoneAwarded();

            // Set a score without restricted mods, which should award the medal
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ruleset_id = 3);

            assertAwarded(medal_id_5_star);
        }

        /// <summary>
        /// This tests the star rating medals, to make sure maps that aren't ranked/approved don't trigger medals.
        /// </summary>
        [Theory]
        [InlineData(BeatmapOnlineStatus.Graveyard)]
        [InlineData(BeatmapOnlineStatus.WIP)]
        [InlineData(BeatmapOnlineStatus.Pending)]
        [InlineData(BeatmapOnlineStatus.Qualified)]
        [InlineData(BeatmapOnlineStatus.Loved)]
        public void TestStarRatingMedalNotGrantedOnInvalidStatuses(BeatmapOnlineStatus onlineStatus)
        {
            const int medal_id_5_star = 59;

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = getNextBeatmapId();
                b.approved = onlineStatus;
            });
            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id);

            addMedal(medal_id_5_star);

            assertNoneAwarded();

            // Set a score on the map, which shouldn't trigger a medal
            SetScoreForBeatmap(beatmap.beatmap_id);

            assertNoneAwarded();
        }

        /// <summary>
        /// This tests the osu!standard combo-based medals.
        /// </summary>
        [Theory]
        [InlineData(BeatmapOnlineStatus.Ranked)]
        [InlineData(BeatmapOnlineStatus.Approved)]
        [InlineData(BeatmapOnlineStatus.Qualified)]
        [InlineData(BeatmapOnlineStatus.Loved)]
        public void TestStandardComboMedal(BeatmapOnlineStatus onlineStatus)
        {
            const int medal_id = 1;

            var beatmap = AddBeatmap(b => b.approved = onlineStatus);

            addMedal(medal_id);

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.max_combo = 499);

            // After passing the beatmap without getting 500 combo, the medal shouldn't be awarded.
            assertNoneAwarded();

            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.max_combo = 500);

            // Once the beatmap is passed with 500 combo, the medal may be awarded.
            assertAwarded(medal_id);
        }

        /// <summary>
        /// This tests the osu!standard combo-based medals.
        /// </summary>
        [Theory]
        [InlineData(BeatmapOnlineStatus.Graveyard)]
        [InlineData(BeatmapOnlineStatus.WIP)]
        [InlineData(BeatmapOnlineStatus.Pending)]
        public void TestComboMedalsNotGivenOnUnrankedBeatmaps(BeatmapOnlineStatus onlineStatus)
        {
            const int medal_id = 1;

            var beatmap = AddBeatmap(b => b.approved = onlineStatus);

            addMedal(medal_id);
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.max_combo = 500);
            assertNoneAwarded();
        }

        /// <summary>
        /// This tests the osu!standard play count-based medals.
        /// </summary>
        [Fact]
        public void TestStandardPlayCountMedal()
        {
            const int medal_id = 20;

            var beatmap = AddBeatmap();

            addMedal(medal_id);

            // Set up user stats with 4998 play count
            using (var db = Processor.GetDatabaseConnection())
            {
                UserStatsOsu stats = new UserStatsOsu
                {
                    user_id = 2,
                    playcount = 4998
                };
                db.Insert(stats);
            }

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id);
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 4999, CancellationToken);

            // After passing the beatmap for the first time we only reach 4999 play count,
            // the medal shouldn't be awarded.
            assertNoneAwarded();

            SetScoreForBeatmap(beatmap.beatmap_id);
            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 5000, CancellationToken);

            // After we pass the map again, play count reaches 5000, the medal may be awarded.
            assertAwarded(medal_id);
        }

        /// <summary>
        /// This tests the hit statistic-based medals used in modes other than osu!standard.
        /// </summary>
        [Fact]
        public void TestHitStatisticMedal()
        {
            const int medal_id = 46;

            var beatmap = AddBeatmap();

            addMedal(medal_id);

            // Set up user stats with 39998 mania key presses
            using (var db = Processor.GetDatabaseConnection())
            {
                UserStatsMania stats = new UserStatsMania
                {
                    user_id = 2,
                    count300 = 39998
                };
                db.Insert(stats);
            }

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ScoreData.Statistics = new Dictionary<HitResult, int> { { HitResult.Perfect, 1 } };
                s.Score.ruleset_id = 3;
            });
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats_mania WHERE user_id = 2", 39999, CancellationToken);

            // After passing the beatmap for the first time we only reach 39999 key count,
            // the medal shouldn't be awarded.
            assertNoneAwarded();

            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ScoreData.Statistics = new Dictionary<HitResult, int> { { HitResult.Perfect, 1 } };
                s.Score.ruleset_id = 3;
            });
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats_mania WHERE user_id = 2", 40000, CancellationToken);

            // After we pass the map again, key count reaches 40000, the medal should be awarded.
            assertAwarded(medal_id);
        }

        private void addMedal(int medalId)
        {
            using (var db = Processor.GetDatabaseConnection())
            {
                db.Execute($"INSERT INTO osu_achievements (achievement_id, slug, ordering, progression, name) VALUES ({medalId}, 'A', 1, 1, 'medal')");
            }
        }

        private void addPackMedal(int medalId, int packId, IReadOnlyList<Beatmap> beatmaps)
        {
            using (var db = Processor.GetDatabaseConnection())
            {
                db.Execute($"INSERT INTO osu_achievements (achievement_id, slug, ordering, progression, name) VALUES ({medalId}, 'A', 1, 1, 'medal')");
                db.Execute($"INSERT INTO osu_beatmappacks (pack_id, url, name, author, tag, date) VALUES ({packId}, 'https://osu.ppy.sh', 'pack', 'wang', 'PACK', NOW())");

                foreach (int setId in beatmaps.GroupBy(b => b.beatmapset_id).Select(g => g.Key))
                    db.Execute($"INSERT INTO osu_beatmappacks_items (pack_id, beatmapset_id) VALUES ({packId}, {setId})");
            }
        }

        private void assertAwarded(int medalId)
        {
            Assert.Contains(awardedMedals, a => medalId == a.Medal.achievement_id);
        }

        private void assertNoneAwarded()
        {
            Assert.Empty(awardedMedals);
        }

        private void assertNotAwarded(int medalId)
        {
            Assert.Collection(awardedMedals, a => Assert.NotEqual(medalId, a.Medal.achievement_id));
        }

        private void onMedalAwarded(MedalProcessor.AwardedMedal awarded)
        {
            awardedMedals.Add(awarded);

            // Usually osu-web would do this.
            using (var db = Processor.GetDatabaseConnection())
            {
                db.Execute($"INSERT INTO osu_user_achievements (achievement_id, user_id, beatmap_id) VALUES ({awarded.Medal.achievement_id}, {awarded.Score.UserID}, {awarded.Score.BeatmapID})");
            }
        }

        public override void Dispose()
        {
            base.Dispose();
            MedalProcessor.MedalAwarded -= onMedalAwarded;
        }
    }
}
