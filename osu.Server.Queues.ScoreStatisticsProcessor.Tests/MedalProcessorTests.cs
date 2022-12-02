// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;
using Xunit;

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

        [Fact]
        public void TestSimplePackAwarding()
        {
            const int medal_id = 7;
            const int pack_id = 40;

            AddBeatmap(b => b.beatmap_id = 71621, s => s.beatmapset_id = 13022);
            AddBeatmap(b => b.beatmap_id = 59225, s => s.beatmapset_id = 16520);
            AddBeatmap(b => b.beatmap_id = 79288, s => s.beatmapset_id = 23073);
            AddBeatmap(b => b.beatmap_id = 101236, s => s.beatmapset_id = 27936);
            AddBeatmap(b => b.beatmap_id = 105325, s => s.beatmapset_id = 32162);
            AddBeatmap(b => b.beatmap_id = 127762, s => s.beatmapset_id = 40233);
            AddBeatmap(b => b.beatmap_id = 132751, s => s.beatmapset_id = 42158);
            AddBeatmap(b => b.beatmap_id = 134948, s => s.beatmapset_id = 42956);
            AddBeatmap(b => b.beatmap_id = 177972, s => s.beatmapset_id = 59370);
            AddBeatmap(b => b.beatmap_id = 204837, s => s.beatmapset_id = 71476);
            AddBeatmap(b => b.beatmap_id = 206298, s => s.beatmapset_id = 72137);
            AddBeatmap(b => b.beatmap_id = 271917, s => s.beatmapset_id = 102913);
            AddBeatmap(b => b.beatmap_id = 514849, s => s.beatmapset_id = 169848);
            AddBeatmap(b => b.beatmap_id = 497769, s => s.beatmapset_id = 211704);

            addPackMedal(medal_id, pack_id, AllBeatmaps);

            foreach (var beatmap in AllBeatmaps)
            {
                assertNotAwarded();
                pushAndInsert(beatmap.beatmap_id);
            }

            assertAwarded(medal_id);

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", AllBeatmaps.Count, CancellationToken);
        }

        private void addPackMedal(int medalId, int packId, IReadOnlyList<Beatmap> beatmaps)
        {
            using (var db = Processor.GetDatabaseConnection())
            {
                db.Execute($"INSERT INTO osu_achievements (achievement_id, slug, ordering, progression, name) VALUES ({medalId}, 'A', 1, 1, 'medal')");
                db.Execute($"INSERT INTO osu_beatmappacks (pack_id, url, name, author, tag, date) VALUES ({packId}, 'https://osu.ppy.sh', 'pack', 'wang', 'PACK', NOW())");

                foreach (var beatmap in beatmaps)
                    db.Execute($"INSERT INTO osu_beatmappacks_items (pack_id, beatmapset_id) VALUES ({packId}, {beatmap.beatmapset_id})");
            }
        }

        private void assertAwarded(int medalId)
        {
            Assert.Collection(awardedMedals, a => Assert.Equal(medalId, a.Medal.achievement_id));
        }

        private void assertNotAwarded()
        {
            Assert.Empty(awardedMedals);
        }

        private void pushAndInsert(int beatmapId)
        {
            using (MySqlConnection conn = Processor.GetDatabaseConnection())
            {
                var score = CreateTestScore(beatmapId: beatmapId);

                conn.Insert(score.Score);
                PushToQueueAndWaitForProcess(score);
            }
        }

        private void onMedalAwarded(MedalProcessor.AwardedMedal awarded)
        {
            awardedMedals.Add(awarded);
        }

        public override void Dispose()
        {
            base.Dispose();
            MedalProcessor.MedalAwarded -= onMedalAwarded;
        }
    }
}
