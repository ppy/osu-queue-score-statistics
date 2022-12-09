// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using Dapper;
using MySqlConnector;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class AddBeatmapTests : DatabaseTest
    {
        [Fact]
        public void TestDefault()
        {
            using var db = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap();
            var databaseBeatmaps = db.Query<Beatmap>("SELECT * FROM osu_beatmaps");

            Assert.Equal(beatmap.beatmap_id, databaseBeatmaps.Single().beatmap_id);
            Assert.Equal(beatmap.beatmapset_id, databaseBeatmaps.Single().beatmapset_id);
        }

        [Fact]
        public void TestChangePropertyOnDefault()
        {
            using var db = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(b => b.diff_overall = 123);
            var databaseBeatmaps = db.Query<Beatmap>("SELECT * FROM osu_beatmaps");

            Assert.Equal(123, databaseBeatmaps.Single().diff_overall);

            Assert.Equal(beatmap.beatmap_id, databaseBeatmaps.Single().beatmap_id);
            Assert.Equal(beatmap.diff_overall, databaseBeatmaps.Single().diff_overall);
        }

        [Fact]
        public void TestAddTwiceFails()
        {
            using var db = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap();

            Assert.Throws<MySqlException>(() => AddBeatmap());

            var databaseBeatmaps = db.Query<Beatmap>("SELECT * FROM osu_beatmaps");

            Assert.Equal(beatmap.beatmap_id, databaseBeatmaps.Single().beatmap_id);
            Assert.Equal(beatmap.beatmapset_id, databaseBeatmaps.Single().beatmapset_id);
        }

        [Fact]
        public void TestSetIdCopied()
        {
            using var db = Processor.GetDatabaseConnection();

            var beatmap = AddBeatmap(beatmapSetSetup: s => s.beatmapset_id = 12345);
            var databaseBeatmaps = db.Query<Beatmap>("SELECT * FROM osu_beatmaps");

            Assert.Equal(beatmap.beatmapset_id, databaseBeatmaps.Single().beatmapset_id);
        }

        [Fact]
        public void TestMismatchSetIdFails()
        {
            Assert.Throws<ArgumentException>(() => AddBeatmap(b => b.beatmapset_id = 98765, s => s.beatmapset_id = 12345));
        }
    }
}
