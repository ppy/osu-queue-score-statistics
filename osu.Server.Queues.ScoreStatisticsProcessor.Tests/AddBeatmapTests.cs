// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class AddBeatmapTests : DatabaseTest
    {
        [Fact]
        public void TestDefault()
        {
            AddBeatmap();

            Assert.Equal(1, AllBeatmaps.Count);
            Assert.Equal(TEST_BEATMAP_ID, AllBeatmaps.Single().beatmap_id);
            Assert.Equal(TEST_BEATMAP_SET_ID, AllBeatmaps.Single().beatmapset_id);
        }

        [Fact]
        public void TestUpdatePropertyOnDefault()
        {
            AddBeatmap(b => b.diff_overall = 123);

            Assert.Equal(1, AllBeatmaps.Count);
            Assert.Equal(TEST_BEATMAP_ID, AllBeatmaps.Single().beatmap_id);
            Assert.Equal(123, AllBeatmaps.Single().diff_overall);
        }

        [Fact]
        public void TestDefaultDoesntAddTwice()
        {
            AddBeatmap();

            Assert.Equal(1, AllBeatmaps.Count);
            Assert.Equal(TEST_BEATMAP_ID, AllBeatmaps.Single().beatmap_id);
            Assert.Equal(TEST_BEATMAP_SET_ID, AllBeatmaps.Single().beatmapset_id);
        }

        [Fact]
        public void TestSetIdCopied()
        {
            AddBeatmap(beatmapSetSetup: s => s.beatmapset_id = 12345);

            Assert.Equal(1, AllBeatmaps.Count);
            Assert.Equal(12345, AllBeatmaps.Single().beatmapset_id);
        }

        [Fact]
        public void TestMismatchSetIdFails()
        {
            Assert.Throws<ArgumentException>(() => AddBeatmap(b => b.beatmapset_id = 98765, s => s.beatmapset_id = 12345));
        }
    }
}
