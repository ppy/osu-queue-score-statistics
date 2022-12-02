// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

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
        }
    }
}
