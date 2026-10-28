// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class MixtapeTest : MedalAwarderTest
    {
        private readonly Beatmap[] beatmaps;

        public MixtapeTest()
        {
            AddMedal(368);

            beatmaps = Enumerable.Range(1, 10).Select(i => AddBeatmap(b =>
            {
                b.beatmap_id = (uint)i;
                b.beatmapset_id = (uint)i;
            }, s => s.beatmapset_id = i)).ToArray();
        }

        [Fact]
        public void MedalAwarded()
        {
            var items = createRoomWithItems(creatorId: 727, "playlists", beatmaps);

            for (int i = 0; i < items.Count - 1; i++)
            {
                SetMultiplayerScoreForBeatmap(beatmaps[i].beatmap_id, items[i]);
                AssertNoMedalsAwarded();
            }

            SetMultiplayerScoreForBeatmap(beatmaps[beatmaps.Length - 1].beatmap_id, items[items.Count - 1]);
            AssertSingleMedalAwarded(368);
        }

        [Fact]
        public void MedalNotAwardedOnOwnPlaylist()
        {
            var items = createRoomWithItems(creatorId: 2, "playlists", beatmaps);

            for (int i = 0; i < items.Count; i++)
                SetMultiplayerScoreForBeatmap(beatmaps[i].beatmap_id, items[i]);

            AssertNoMedalsAwarded();
        }

        [Fact]
        public void MedalNotAwardedOnPlaylistWithLessThanTenItems()
        {
            var items = createRoomWithItems(creatorId: 727, "playlists", beatmaps.Take(9).ToArray());

            for (int i = 0; i < items.Count; i++)
                SetMultiplayerScoreForBeatmap(beatmaps[i].beatmap_id, items[i]);

            AssertNoMedalsAwarded();
        }

        [Fact]
        public void MedalNotAwardedInRealtimeRoom()
        {
            var items = createRoomWithItems(creatorId: 727, "head_to_head", beatmaps);

            for (int i = 0; i < items.Count; i++)
                SetMultiplayerScoreForBeatmap(beatmaps[i].beatmap_id, items[i]);

            AssertNoMedalsAwarded();
        }

        [Fact]
        public void MedalNotAwardedIfFailedScoresDoNotCompletePlaylist()
        {
            var items = createRoomWithItems(creatorId: 727, "playlists", beatmaps);

            SetMultiplayerScoreForBeatmap(beatmaps[0].beatmap_id, items[0], s => s.Score.passed = false);

            for (int i = 1; i < items.Count; i++)
                SetMultiplayerScoreForBeatmap(beatmaps[i].beatmap_id, items[i]);

            AssertNoMedalsAwarded();

            SetMultiplayerScoreForBeatmap(beatmaps[0].beatmap_id, items[0]);
            AssertSingleMedalAwarded(368);
        }

        [Fact]
        public void RepeatedPassesOnSameItemDoNotCountTwice()
        {
            var items = createRoomWithItems(creatorId: 727, "playlists", beatmaps);

            for (int i = 0; i < items.Count - 1; i++)
            {
                SetMultiplayerScoreForBeatmap(beatmaps[i].beatmap_id, items[i]);
                SetMultiplayerScoreForBeatmap(beatmaps[i].beatmap_id, items[i]);
            }

            AssertNoMedalsAwarded();

            SetMultiplayerScoreForBeatmap(beatmaps[beatmaps.Length - 1].beatmap_id, items[items.Count - 1]);
            AssertSingleMedalAwarded(368);
        }

        [Fact]
        public void DuplicateBeatmapItemsCountSeparately()
        {
            var items = createRoomWithItems(creatorId: 727, "playlists", Enumerable.Repeat(beatmaps[0], 10).ToArray());

            for (int i = 0; i < items.Count - 1; i++)
            {
                SetMultiplayerScoreForBeatmap(beatmaps[0].beatmap_id, items[i]);
                AssertNoMedalsAwarded();
            }

            SetMultiplayerScoreForBeatmap(beatmaps[0].beatmap_id, items[items.Count - 1]);
            AssertSingleMedalAwarded(368);
        }

        [Fact]
        public void MedalNotAwardedForSoloScores()
        {
            foreach (var beatmap in beatmaps)
                SetScoreForBeatmap(beatmap.beatmap_id);

            AssertNoMedalsAwarded();
        }

        private List<ulong> createRoomWithItems(uint creatorId, string roomType, Beatmap[] roomBeatmaps)
        {
            ulong roomId = CreateMultiplayerRoom("room", roomType, userId: creatorId);
            return roomBeatmaps.Select(b => CreatePlaylistItem(b, roomId)).ToList();
        }
    }
}
