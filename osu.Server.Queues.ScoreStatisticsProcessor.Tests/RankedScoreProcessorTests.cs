// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Beatmaps;
using osu.Game.Rulesets.Scoring;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class RankedScoreProcessorTests : DatabaseTest
    {
        [Fact]
        public void TestNonPassingScoreDoesNothing()
        {
            AddBeatmap();
            waitForRankedScore("osu_user_stats", 0);

            SetScoreForBeatmap(TEST_BEATMAP_ID, item => item.Score.passed = false);
            waitForRankedScore("osu_user_stats", 0);
        }

        [Theory]
        [InlineData(BeatmapOnlineStatus.Graveyard)]
        [InlineData(BeatmapOnlineStatus.WIP)]
        [InlineData(BeatmapOnlineStatus.Pending)]
        [InlineData(BeatmapOnlineStatus.Qualified)]
        public void TestScoreOnUnrankedMapDoesNothing(BeatmapOnlineStatus status)
        {
            AddBeatmap(b => b.approved = status);
            waitForRankedScore("osu_user_stats", 0);

            SetScoreForBeatmap(TEST_BEATMAP_ID);
            waitForRankedScore("osu_user_stats", 0);
        }

        [Theory]
        [InlineData(BeatmapOnlineStatus.Ranked)]
        [InlineData(BeatmapOnlineStatus.Approved)]
        [InlineData(BeatmapOnlineStatus.Loved)]
        public void TestScoreOnRankedMapIncreasesRankedScore(BeatmapOnlineStatus status)
        {
            AddBeatmap(b => b.approved = status);
            waitForRankedScore("osu_user_stats", 0);

            SetScoreForBeatmap(TEST_BEATMAP_ID);
            waitForRankedScore("osu_user_stats", 10081);
        }

        [Fact]
        public void TestUnrankedScoreOnRankedMapDoesNotIncreaseRankedScore()
        {
            AddBeatmap(b => b.approved = BeatmapOnlineStatus.Ranked);
            waitForRankedScore("osu_user_stats", 0);

            SetScoreForBeatmap(TEST_BEATMAP_ID, s => s.Score.ranked = false);
            waitForRankedScore("osu_user_stats", 0);

            SetScoreForBeatmap(TEST_BEATMAP_ID, s => s.Score.total_score = 50000);
            waitForRankedScore("osu_user_stats", 5041);
        }

        [Fact]
        public void TestScoresFromDifferentBeatmapsAreCountedSeparately()
        {
            var firstBeatmap = AddBeatmap(b => b.beatmap_id = 1001, s => s.beatmapset_id = 1);
            var secondBeatmap = AddBeatmap(b => b.beatmap_id = 1002, s => s.beatmapset_id = 2);

            waitForRankedScore("osu_user_stats", 0);

            SetScoreForBeatmap(firstBeatmap.beatmap_id);
            waitForRankedScore("osu_user_stats", 10081);

            SetScoreForBeatmap(secondBeatmap.beatmap_id);
            waitForRankedScore("osu_user_stats", 20162);
        }

        [Fact]
        public void TestScoresFromSameBeatmapInDifferentRulesetsAreCountedSeparately()
        {
            AddBeatmap();
            waitForRankedScore("osu_user_stats", 0);
            waitForRankedScore("osu_user_stats_mania", 0);

            SetScoreForBeatmap(TEST_BEATMAP_ID);
            waitForRankedScore("osu_user_stats", 10081);
            waitForRankedScore("osu_user_stats_mania", 0);

            SetScoreForBeatmap(TEST_BEATMAP_ID, item => item.Score.ruleset_id = item.Score.ruleset_id = 3);
            waitForRankedScore("osu_user_stats", 10081);
            waitForRankedScore("osu_user_stats_mania", 100000);
        }

        [Fact]
        public void TestWorseScoreIsNotCounted()
        {
            AddBeatmap();
            waitForRankedScore("osu_user_stats", 0);

            SetScoreForBeatmap(TEST_BEATMAP_ID);
            waitForRankedScore("osu_user_stats", 10081);

            SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.total_score = 50000;
                score.Score.ScoreData.Statistics[HitResult.Perfect] = 0;
                score.Score.ScoreData.Statistics[HitResult.Ok] = 5;
            });
            waitForRankedScore("osu_user_stats", 10081);
        }

        [Fact]
        public void TestBetterScoreReplacesWorseScore()
        {
            AddBeatmap();
            waitForRankedScore("osu_user_stats", 0);

            SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.total_score = 50000;
                score.Score.ScoreData.Statistics[HitResult.Perfect] = 0;
                score.Score.ScoreData.Statistics[HitResult.Ok] = 5;
            });
            waitForRankedScore("osu_user_stats", 5041);

            SetScoreForBeatmap(TEST_BEATMAP_ID);
            waitForRankedScore("osu_user_stats", 10081);
        }

        [Fact]
        public void TestReprocessWithSameVersionDoesntIncrease()
        {
            AddBeatmap();

            waitForRankedScore("osu_user_stats", 0);

            var score = SetScoreForBeatmap(TEST_BEATMAP_ID);
            waitForRankedScore("osu_user_stats", 10081);

            // the score will be marked as processed (in the database) at this point, so should not increase ranked score if processed a second time.
            score.MarkProcessed();

            PushToQueueAndWaitForProcess(score);
            waitForRankedScore("osu_user_stats", 10081);
        }

        [Fact]
        public void TestReprocessNewHighScoreDoesNotChangeRankedTotal()
        {
            AddBeatmap();

            waitForRankedScore("osu_user_stats", 0);

            SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.total_score = 50000;
                score.Score.ScoreData.Statistics[HitResult.Perfect] = 0;
                score.Score.ScoreData.Statistics[HitResult.Ok] = 5;
            });
            waitForRankedScore("osu_user_stats", 5041);

            var secondScore = SetScoreForBeatmap(TEST_BEATMAP_ID);
            waitForRankedScore("osu_user_stats", 10081);

            // the score will be marked as processed (in the database) at this point.
            secondScore.MarkProcessed();
            // artificially increase the `processed_version` so that the score undergoes a revert and reprocess.
            secondScore.ProcessHistory!.processed_version++;

            PushToQueueAndWaitForProcess(secondScore);

            waitForRankedScore("osu_user_stats", 10081);
        }

        [Fact]
        public void TestReprocessNewNonHighScoreDoesNotChangeRankedTotal()
        {
            AddBeatmap();

            waitForRankedScore("osu_user_stats", 0);

            SetScoreForBeatmap(TEST_BEATMAP_ID);
            waitForRankedScore("osu_user_stats", 10081);

            var secondScore = SetScoreForBeatmap(TEST_BEATMAP_ID, score =>
            {
                score.Score.total_score = 50000;
                score.Score.ScoreData.Statistics[HitResult.Perfect] = 0;
                score.Score.ScoreData.Statistics[HitResult.Ok] = 5;
            });
            waitForRankedScore("osu_user_stats", 10081);

            // the score will be marked as processed (in the database) at this point.
            secondScore.MarkProcessed();
            // artificially increase the `processed_version` so that the score undergoes a revert and reprocess.
            secondScore.ProcessHistory!.processed_version++;

            PushToQueueAndWaitForProcess(secondScore);

            waitForRankedScore("osu_user_stats", 10081);
        }

        private void waitForRankedScore(string tableName, long expectedRankedScore)
            => WaitForDatabaseState($"SELECT `ranked_score` FROM {tableName} WHERE `user_id` = 2", expectedRankedScore, CancellationToken);
    }
}
