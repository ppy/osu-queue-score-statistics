// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Text;
using Dapper;
using osu.Game.Scoring;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class ManiaKeyModeUserStatsProcessorTests : DatabaseTest
    {
        public ManiaKeyModeUserStatsProcessorTests()
        {
            using (var db = Processor.GetDatabaseConnection())
            {
                db.Execute("TRUNCATE TABLE `phpbb_users`");
                db.Execute("TRUNCATE TABLE `osu_user_stats_mania_4k`");
                db.Execute("TRUNCATE TABLE `osu_user_stats_mania_7k`");

                db.Execute("INSERT INTO `phpbb_users` (`user_id`, `username`, `country_acronym`, `user_permissions`, `user_sig`, `user_occ`, `user_interests`) VALUES (2, 'test', 'JP', '', '', '', '')");
            }
        }

        [Fact]
        public void NonManiaPlaysAreIgnored()
        {
            WaitForDatabaseState("SELECT COUNT(1) FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", 0, CancellationToken, new { userId = 2 });
            WaitForDatabaseState("SELECT COUNT(1) FROM `osu_user_stats_mania_7k` WHERE `user_id` = @userId", 0, CancellationToken, new { userId = 2 });

            var beatmap = AddBeatmap(b => b.beatmap_id = 10);

            SetScoreForBeatmap(beatmap.beatmap_id);

            WaitForDatabaseState("SELECT COUNT(1) FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", 0, CancellationToken, new { userId = 2 });
            WaitForDatabaseState("SELECT COUNT(1) FROM `osu_user_stats_mania_7k` WHERE `user_id` = @userId", 0, CancellationToken, new { userId = 2 });
        }

        [Fact]
        public void PlaysOnMapsWithWrongKeyCountAreIgnored()
        {
            WaitForDatabaseState("SELECT COUNT(1) FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", 0, CancellationToken, new { userId = 2 });
            WaitForDatabaseState("SELECT COUNT(1) FROM `osu_user_stats_mania_7k` WHERE `user_id` = @userId", 0, CancellationToken, new { userId = 2 });

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 11;
                b.playmode = 3;
                b.diff_size = 6;
            });

            SetScoreForBeatmap(beatmap.beatmap_id, s => s.Score.ruleset_id = 3);

            WaitForDatabaseState("SELECT COUNT(1) FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", 0, CancellationToken, new { userId = 2 });
            WaitForDatabaseState("SELECT COUNT(1) FROM `osu_user_stats_mania_7k` WHERE `user_id` = @userId", 0, CancellationToken, new { userId = 2 });
        }

        [Fact]
        public void PlaysOnMapsWithCorrectKeyCountAreCounted()
        {
            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", (int?)null, CancellationToken, new { userId = 2 });
            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_7k` WHERE `user_id` = @userId", (int?)null, CancellationToken, new { userId = 2 });

            var beatmap4K = AddBeatmap(b =>
            {
                b.beatmap_id = 12;
                b.playmode = 3;
                b.diff_size = 4;
            }, s => s.beatmapset_id = 1);
            var beatmap7K = AddBeatmap(b =>
            {
                b.beatmap_id = 13;
                b.playmode = 3;
                b.diff_size = 7;
            }, s => s.beatmapset_id = 2);

            SetScoreForBeatmap(beatmap4K.beatmap_id, s => s.Score.ruleset_id = 3);

            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", 1, CancellationToken, new { userId = 2 });
            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_7k` WHERE `user_id` = @userId", (int?)null, CancellationToken, new { userId = 2 });

            SetScoreForBeatmap(beatmap7K.beatmap_id, s => s.Score.ruleset_id = 3);

            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", 1, CancellationToken, new { userId = 2 });
            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_7k` WHERE `user_id` = @userId", 1, CancellationToken, new { userId = 2 });

            SetScoreForBeatmap(beatmap7K.beatmap_id, s => s.Score.ruleset_id = 3);

            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", 1, CancellationToken, new { userId = 2 });
            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_7k` WHERE `user_id` = @userId", 2, CancellationToken, new { userId = 2 });
        }

        [Fact]
        public void RankCountsCorrect()
        {
            var beatmap4K = AddBeatmap(b =>
            {
                b.beatmap_id = 12;
                b.playmode = 3;
                b.diff_size = 4;
            }, s => s.beatmapset_id = 1);
            var beatmap7K = AddBeatmap(b =>
            {
                b.beatmap_id = 13;
                b.playmode = 3;
                b.diff_size = 7;
            }, s => s.beatmapset_id = 2);

            WaitForDatabaseState<(int?, int?)>("SELECT `a_rank_count`, `s_rank_count` FROM `osu_user_stats_mania_7k` WHERE `user_id` = @userId", (null, null), CancellationToken, new { userId = 2 });
            WaitForDatabaseState<(int?, int?)>("SELECT `a_rank_count`, `s_rank_count` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", (null, null), CancellationToken, new { userId = 2 });

            SetScoreForBeatmap(beatmap4K.beatmap_id, s =>
            {
                s.Score.ranked = s.Score.preserve = true;
                s.Score.total_score = 500_000;
                s.Score.rank = ScoreRank.A;
                s.Score.ruleset_id = 3;
            });

            WaitForDatabaseState<(int?, int?)>("SELECT `a_rank_count`, `s_rank_count` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", (1, 0), CancellationToken, new { userId = 2 });

            SetScoreForBeatmap(beatmap4K.beatmap_id, s =>
            {
                s.Score.ranked = s.Score.preserve = true;
                s.Score.total_score = 300_000; // same map and keymode as above, lower score => should not count
                s.Score.rank = ScoreRank.S;
                s.Score.ruleset_id = 3;
            });

            WaitForDatabaseState<(int?, int?)>("SELECT `a_rank_count`, `s_rank_count` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", (1, 0), CancellationToken, new { userId = 2 });

            SetScoreForBeatmap(beatmap4K.beatmap_id, s =>
            {
                s.Score.ranked = s.Score.preserve = true;
                s.Score.total_score = 700_000; // same map and keymode as above, higher score => should count
                s.Score.rank = ScoreRank.S;
                s.Score.ruleset_id = 3;
            });

            WaitForDatabaseState<(int?, int?)>("SELECT `a_rank_count`, `s_rank_count` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", (0, 1), CancellationToken, new { userId = 2 });

            SetScoreForBeatmap(beatmap7K.beatmap_id, s =>
            {
                s.Score.ranked = s.Score.preserve = true;
                s.Score.ruleset_id = 3;
                s.Score.rank = ScoreRank.A;
            });

            WaitForDatabaseState<(int?, int?)>("SELECT `a_rank_count`, `s_rank_count` FROM `osu_user_stats_mania_7k` WHERE `user_id` = @userId", (1, 0), CancellationToken, new { userId = 2 });
            WaitForDatabaseState<(int?, int?)>("SELECT `a_rank_count`, `s_rank_count` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", (0, 1), CancellationToken, new { userId = 2 });
        }

        [Fact]
        public void PerformancePointTotalAndRankUpdated()
        {
            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", (int?)null, CancellationToken, new { userId = 2 });

            using (var db = Processor.GetDatabaseConnection())
            {
                // simulate fake users to beat as we climb up ranks.
                // this is going to be a bit of a chonker query...
                var stringBuilder = new StringBuilder();

                stringBuilder.Append("INSERT INTO `osu_user_stats_mania_4k` (`user_id`, `rank_score`, `rank_score_index`, `accuracy_new`, `playcount`, "
                                     + "`x_rank_count`, `xh_rank_count`, `s_rank_count`, `sh_rank_count`, `a_rank_count`) VALUES ");

                for (int i = 0; i < 100; ++i)
                {
                    if (i > 0)
                        stringBuilder.Append(',');

                    stringBuilder.Append($"({100 + i}, {100 - i}, {i}, 1, 0, 0, 0, 0, 0, 0)");
                }

                db.Execute(stringBuilder.ToString());
            }

            var beatmap = AddBeatmap(b =>
            {
                b.beatmap_id = 12;
                b.playmode = 3;
                b.diff_size = 4;
            });

            WaitForDatabaseState<bool?>("SELECT `rank_score` > 0 FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", null, CancellationToken, new { userId = 2 });
            WaitForDatabaseState<int?>("SELECT `rank_score_index` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", null, CancellationToken, new { userId = 2 });

            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.ranked = s.Score.preserve = true;
                s.Score.ruleset_id = 3;
                s.Score.pp = 50;
            });

            WaitForDatabaseState<bool?>("SELECT `rank_score` > 0 FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", true, CancellationToken, new { userId = 2 });
            WaitForDatabaseState<int?>("SELECT `rank_score_index` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", 49, CancellationToken, new { userId = 2 });
        }

        [Fact]
        public void BackfillPlaycountEstimate()
        {
            var beatmapMania4K = AddBeatmap(b =>
            {
                b.beatmap_id = 100;
                b.playmode = 3;
                b.diff_size = 4;
            }, s => s.beatmapset_id = 10);

            var beatmapMania7K = AddBeatmap(b =>
            {
                b.beatmap_id = 101;
                b.playmode = 3;
                b.diff_size = 7;
            }, s => s.beatmapset_id = 11);

            var beatmapOsu = AddBeatmap(b =>
            {
                b.beatmap_id = 102;
            }, s => s.beatmapset_id = 12);

            using (var db = Processor.GetDatabaseConnection())
            {
                // insert some scores manually without pushing them for processing.
                // this is because we wish to exercise the initial estimation logic, which will only run the first time round.
                var stringBuilder = new StringBuilder();

                stringBuilder.Append("INSERT INTO `scores` (`id`, `user_id`, `ruleset_id`, `beatmap_id`, `has_replay`, `preserve`, `ranked`, `rank`, "
                                     + "`passed`, `accuracy`, `max_combo`, `total_score`, `data`, `pp`, `legacy_score_id`, `legacy_total_score`, "
                                     + "`started_at`, `ended_at`, `build_id`) VALUES ");

                const string score_data = @"{""mods"": [], ""statistics"": {""perfect"": 5, ""large_bonus"": 0}, ""maximum_statistics"": {""perfect"": 5, ""large_bonus"": 2}}";
                var endedAt = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

                // 4k scores
                for (int i = 0; i < 19; ++i)
                {
                    if (i > 0)
                        stringBuilder.Append(',');

                    stringBuilder.Append($"({i + 1}, 2, 3, {beatmapMania4K.beatmap_id}, 0, 1, 1, 'S', 1, 1, 100, 1000000, '{score_data}', NULL, NULL, 0, '{endedAt:O}', '{endedAt:O}', NULL)");
                }

                // 7k scores
                for (int i = 0; i < 30; ++i)
                {
                    stringBuilder.Append(',');
                    stringBuilder.Append($"({i + 20}, 2, 3, {beatmapMania7K.beatmap_id}, 0, 1, 1, 'S', 1, 1, 100, 1000000, '{score_data}', NULL, NULL, 0, '{endedAt:O}', '{endedAt:O}', NULL)");
                }

                // osu! scores
                for (int i = 0; i < 50; ++i)
                {
                    stringBuilder.Append(',');
                    stringBuilder.Append($"({i + 50}, 2, 0, {beatmapOsu.beatmap_id}, 0, 1, 1, 'S', 1, 1, 100, 1000000, '{score_data}', NULL, NULL, 0, '{endedAt:O}', '{endedAt:O}', NULL)");
                }

                db.Execute(stringBuilder.ToString());

                db.Execute("INSERT INTO `osu_user_stats_mania` (`user_id`, `rank_score`, `rank_score_index`, "
                           + "`accuracy_total`, `accuracy_count`, `accuracy`, `accuracy_new`, `playcount`, `ranked_score`, `total_score`, "
                           + "`x_rank_count`, `xh_rank_count`, `s_rank_count`, `sh_rank_count`, `a_rank_count`, `rank`, `level`) VALUES "
                           + "(2, 0, 0, 0, 0, 1, 1, 99, 0, 0, 0, 0, 0, 0, 0, 0, 1)");
            }

            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", (int?)null, CancellationToken, new { userId = 2 });
            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_7k` WHERE `user_id` = @userId", (int?)null, CancellationToken, new { userId = 2 });

            SetScoreForBeatmap(beatmapMania4K.beatmap_id, s =>
            {
                s.Score.id = 100;
                s.Score.ranked = s.Score.preserve = true;
                s.Score.ruleset_id = 3;
            });
            WaitForDatabaseState("SELECT `playcount` FROM `osu_user_stats_mania_4k` WHERE `user_id` = @userId", 40, CancellationToken, new { userId = 2 }); // 20 / (20 + 30) * 100
        }
    }
}
