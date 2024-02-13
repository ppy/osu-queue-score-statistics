// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Dapper.Contrib.Extensions;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Rulesets.Mania.Difficulty;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Taiko.Difficulty;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;
using Xunit;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class MedalProcessorTests : DatabaseTest
    {
        private readonly List<MedalProcessor.AwardedMedal> awardedMedals = new List<MedalProcessor.AwardedMedal>();

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
        /// The medal processor should skip medals which have already been awarded.
        /// There are no medals which should trigger more than once.
        /// </summary>
        [Fact]
        public void TestOnlyAwardsOnce()
        {
            var beatmap = AddBeatmap();

            const int medal_id = 7;
            const int pack_id = 40;

            addPackMedal(medal_id, pack_id, new[] { beatmap });

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id);

            assertAwarded(medal_id);

            SetScoreForBeatmap(beatmap.beatmap_id);
            assertAwarded(medal_id);
        }

        /// <summary>
        /// The pack awarder should skip scores that are failed.
        /// </summary>
        [Fact]
        public void TestDoesNotAwardOnFailedScores()
        {
            var beatmap = AddBeatmap();

            const int medal_id = 7;
            const int pack_id = 40;

            addPackMedal(medal_id, pack_id, new[] { beatmap });

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.passed = false);

            assertNoneAwarded();
        }

        /// <summary>
        /// This tests the simplest case of a medal being awarded for completing a pack.
        /// This mimics the "video game" pack, but is intended to test the process rather than the
        /// content of that pack specifically.
        /// </summary>
        [Fact]
        public void TestSimplePack()
        {
            const int medal_id = 7;
            const int pack_id = 40;

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

            addPackMedal(medal_id, pack_id, allBeatmaps);

            // Need to space out submissions else we will hit rate limits.
            int minutesOffset = -allBeatmaps.Length;

            foreach (var beatmap in allBeatmaps)
            {
                assertNoneAwarded();
                SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ended_at = DateTimeOffset.Now.AddMinutes(minutesOffset++));
            }

            assertAwarded(medal_id);

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", allBeatmaps.Length, CancellationToken);
        }

        /// <summary>
        /// Beatmap packs are defined as a list of beatmap *set* IDs.
        /// When checking whether we should award, there's a need group user's plays across a single set to avoid counting
        /// plays on different difficulties of the same beatmap twice.
        /// </summary>
        [Fact]
        public void TestPlayMultipleBeatmapsFromSameSetDoesNotAward()
        {
            const int medal_id = 7;
            const int pack_id = 40;
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

            addPackMedal(medal_id, pack_id, beatmaps);

            foreach (var beatmap in beatmaps)
            {
                assertNoneAwarded();
                SetScoreForBeatmap(beatmap.beatmap_id);
            }

            // Awarding should only happen after the final set is hit.
            assertAwarded(medal_id);
        }

        /// <summary>
        /// We may have multiple scores in the database for a single user-beatmap combo.
        /// Only one should be counted.
        /// </summary>
        [Fact]
        public void TestPlayMultipleTimeOnSameSetDoesNotAward()
        {
            const int medal_id = 7;
            const int pack_id = 40;

            var beatmap1 = AddBeatmap(b => b.beatmap_id = 71623, s => s.beatmapset_id = 13022);
            var beatmap2 = AddBeatmap(b => b.beatmap_id = 59225, s => s.beatmapset_id = 16520);

            addPackMedal(medal_id, pack_id, new[] { beatmap1, beatmap2 });

            SetScoreForBeatmap(beatmap1.beatmap_id);
            assertNoneAwarded();
            SetScoreForBeatmap(beatmap1.beatmap_id);
            assertNoneAwarded();
            SetScoreForBeatmap(beatmap1.beatmap_id);
            assertNoneAwarded();

            SetScoreForBeatmap(beatmap2.beatmap_id);
            assertAwarded(medal_id);
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

            foreach (var beatmap in allBeatmaps)
            {
                assertNoneAwarded();
                SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = new[] { new APIMod(new OsuModEasy()) });
            }

            // Even after completing all beatmaps with easy mod, the pack medal is not awarded.
            assertNoneAwarded();

            foreach (var beatmap in allBeatmaps)
            {
                assertNoneAwarded();
                SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = new[] { new APIMod(new OsuModDoubleTime()) });
            }

            // Only after completing each beatmap again without easy mod (double time arbitrarily added to mix things up)
            // is the pack actually awarded.
            assertAwarded(medal_id);

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", allBeatmaps.Length * 2, CancellationToken);
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

        /// <summary>
        /// This tests whether Mod Introduction medals are properly awarded when classic mod is enabled.
        /// </summary>
        [Fact]
        public void TestModIntroductionWithClassic()
        {
            const int medal_id = 122;

            var beatmap = AddBeatmap();

            addMedal(medal_id);

            assertNoneAwarded();

            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ScoreData.Mods = new[] { new APIMod(new OsuModDoubleTime()), new APIMod(new OsuModClassic()) });

            // When the beatmap is completed with double time and classic, the medal should be awarded.
            assertAwarded(medal_id);
        }

        /// <summary>
        /// This tests the star rating medals, both pass and FC.
        /// </summary>
        [Fact]
        public void TestStarRatingMedals()
        {
            const int medal_id_pass = 59;
            const int medal_id_fc = 67;

            // BeatmapStore (used in StarRatingMedalAwarder) may cache beatmap and difficulty information,
            // in order to avoid using stale data, we use beatmap ID 2
            var beatmap = AddBeatmap(b => b.beatmap_id = 2);
            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id);

            addMedal(medal_id_pass);
            addMedal(medal_id_fc);

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ScoreData.Statistics = new()
                {
                    { HitResult.Perfect, 3 },
                    { HitResult.Miss, 2 },
                    { HitResult.LargeBonus, 0 }
                };
                s.Score.ScoreData.MaximumStatistics = new()
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
                s.Score.ScoreData.Statistics = new()
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 2 }
                };
                s.Score.ScoreData.MaximumStatistics = new()
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

            // BeatmapStore (used in StarRatingMedalAwarder) may cache beatmap and difficulty information,
            // in order to avoid using stale data, we use beatmap ID 2
            var beatmap = AddBeatmap(b => b.beatmap_id = 2);
            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id);

            addMedal(medal_id_pass);
            addMedal(medal_id_fc);

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ScoreData.Statistics = new()
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 2 }
                };
                s.Score.ScoreData.MaximumStatistics = new()
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

            // BeatmapStore (used in StarRatingMedalAwarder) may cache beatmap and difficulty information,
            // in order to avoid using stale data, we use beatmap ID 2
            var beatmap = AddBeatmap(b => b.beatmap_id = 2);
            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id);

            addMedal(medal_id_5_star);
            addMedal(medal_id_4_star);

            assertNoneAwarded();
            SetScoreForBeatmap(beatmap.beatmap_id);

            // After passing the 5 star beatmap, the 5 star medal is awarded, while the 4 star one is not.
            assertAwarded(medal_id_5_star);
            assertNotAwarded(medal_id_4_star);
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

            // BeatmapStore (used in StarRatingMedalAwarder) may cache beatmap and difficulty information,
            // in order to avoid using stale data, we use beatmap ID 2
            var beatmap = AddBeatmap(b => b.beatmap_id = 2);
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
        /// This tests the star rating medals, to make sure loved maps don't trigger medals.
        /// </summary>
        [Fact]
        public void TestStarRatingMedalLovedMaps()
        {
            const int medal_id_5_star = 59;

            // BeatmapStore (used in StarRatingMedalAwarder) may cache beatmap and difficulty information,
            // in order to avoid using stale data, we use beatmap ID 3 (ID 2 may have a cached ranked status)
            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 3;
                b.approved = BeatmapOnlineStatus.Loved;
            });
            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id);

            addMedal(medal_id_5_star);

            assertNoneAwarded();

            // Set a score on the loved map, which shouldn't trigger a medal
            SetScoreForBeatmap(beatmap.beatmap_id);

            assertNoneAwarded();
        }

        /// <summary>
        /// This tests the osu!standard combo-based medals.
        /// </summary>
        [Fact]
        public void TestStandardComboMedal()
        {
            const int medal_id = 1;

            var beatmap = AddBeatmap();

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
                s.Score.ScoreData.Statistics = new() { { HitResult.Perfect, 1 } };
                s.Score.ruleset_id = 3;
            });
            WaitForDatabaseState("SELECT count300 FROM osu_user_stats_mania WHERE user_id = 2", 39999, CancellationToken);

            // After passing the beatmap for the first time we only reach 39999 key count,
            // the medal shouldn't be awarded.
            assertNoneAwarded();

            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ScoreData.Statistics = new() { { HitResult.Perfect, 1 } };
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
