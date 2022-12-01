// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;
using Xunit.Sdk;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    [Collection("Database tests")] // Ensure all tests hitting the database are run sequentially (no parallel execution).
    public abstract class DatabaseTest : IDisposable
    {
        protected readonly ScoreStatisticsProcessor Processor;

        protected CancellationToken CancellationToken => cancellationSource.Token;

        protected const int MAX_COMBO = 1337;

        protected const int TEST_BEATMAP_ID = 172;

        private readonly CancellationTokenSource cancellationSource = new CancellationTokenSource(10000);

        protected DatabaseTest()
        {
            Processor = new ScoreStatisticsProcessor();
            Processor.ClearQueue();

            using (var db = Processor.GetDatabaseConnection())
            {
                // just a safety measure for now to ensure we don't hit production. since i was running on production until now.
                // will throw if not on test database.
                if (db.QueryFirstOrDefault<int?>("SELECT * FROM osu_counts WHERE name = 'is_production'") != null)
                    throw new InvalidOperationException("You are trying to do something very silly.");

                db.Execute("TRUNCATE TABLE osu_user_stats");
                db.Execute("TRUNCATE TABLE osu_user_stats_mania");
                db.Execute("TRUNCATE TABLE osu_user_beatmap_playcount");
                db.Execute("TRUNCATE TABLE osu_user_month_playcount");
                db.Execute($"TRUNCATE TABLE {SoloScore.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {ProcessHistory.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {SoloScorePerformance.TABLE_NAME}");

                AddBeatmap(TEST_BEATMAP_ID, 76);
            }

            Task.Run(() => Processor.Run(CancellationToken), CancellationToken);
        }

        private static ulong scoreIDSource;

        public static ScoreItem CreateTestScore(int rulesetId = 0)
        {
            var row = new SoloScore
            {
                id = Interlocked.Increment(ref scoreIDSource),
                user_id = 2,
                beatmap_id = TEST_BEATMAP_ID,
                ruleset_id = rulesetId,
                created_at = DateTimeOffset.Now,
                updated_at = DateTimeOffset.Now,
            };

            var startTime = new DateTimeOffset(new DateTime(2020, 02, 05));
            var scoreInfo = new SoloScoreInfo
            {
                ID = row.id,
                UserID = row.user_id,
                BeatmapID = row.beatmap_id,
                RulesetID = row.ruleset_id,
                StartedAt = startTime,
                EndedAt = startTime + TimeSpan.FromSeconds(180),
                MaxCombo = MAX_COMBO,
                TotalScore = 100000,
                Rank = ScoreRank.S,
                Statistics =
                {
                    { HitResult.Perfect, 5 },
                    { HitResult.LargeBonus, 0 }
                },
                Passed = true
            };

            row.ScoreInfo = scoreInfo;

            return new ScoreItem(row);
        }

        protected void AddBeatmap(int beatmapId, int beatmapSetId)
        {
            using (var db = Processor.GetDatabaseConnection())
            {
                db.Execute(
                    $@"INSERT IGNORE INTO osu.osu_beatmaps (beatmap_id, beatmapset_id, user_id, filename, checksum, version, total_length, hit_length, countTotal, countNormal, countSlider, countSpinner, diff_drain, diff_size, diff_overall, diff_approach, playmode, approved, last_update, difficultyrating, playcount, passcount, youtube_preview, score_version, deleted_at, bpm) VALUES ({beatmapId}, {beatmapSetId}, 857, 'Sakamoto Maaya - Kazemachi Jet (KiraCatgirl).osu', '44abebf44f8c91189e679206615638a4', 'Normal', 158, 157, 227, 162, 63, 2, 5, 5, 5, 5, 0, 1, '2014-05-18 17:02:29', 2.29527, 34183, 7159, null, 1, null, 109.02);");

                db.Execute(
                    $@"INSERT IGNORE INTO osu.osu_beatmapsets (beatmapset_id, user_id, thread_id, artist, artist_unicode, title, title_unicode, creator, source, tags, video, storyboard, epilepsy, bpm, versions_available, approved, approvedby_id, approved_date, submit_date, last_update, filename, active, rating, offset, displaytitle, genre_id, language_id, star_priority, filesize, filesize_novideo, body_hash, header_hash, osz2_hash, download_disabled, download_disabled_url, thread_icon_date, favourite_count, play_count, difficulty_names, cover_updated_at, discussion_enabled, discussion_locked, deleted_at, hype, nominations, previous_queue_duration, queued_at, storyboard_hash, nsfw, track_id, spotlight, comment_locked) VALUES ({beatmapSetId}, 857, 283, 'Sakamoto Maaya', null, 'Kazemachi Jet', null, 'KiraCatgirl', '', '', 0, 0, 0, 109.03, 1, 1, null, '2007-10-11 17:39:44', '2007-10-11 17:39:44', '2007-10-11 17:39:44', 'Sakamoto Maaya - Kazemachi Jet.osz', 1, 8.19219, 0, '[bold:0,size:20]Sakamoto Maaya|Kazemachi Jet', 3, 3, 0, 5487373, null, null, null, null, 0, null, null, 8, 34183, 'Normal ★2.3@0', '2021-05-26 08:33:05', 1, 0, null, 0, 0, 0, '2007-10-12 01:39:44', null, 0, null, 0, 0);");
            }
        }

        protected void WaitForTotalProcessed(int count, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Processor.TotalProcessed == count)
                    return;

                Thread.Sleep(50);
            }

            throw new XunitException("All scores were not successfully processed");
        }

        protected void WaitForDatabaseState<T>(string sql, T expected, CancellationToken cancellationToken)
        {
            using (var db = Processor.GetDatabaseConnection())
            {
                while (true)
                {
                    if (!Debugger.IsAttached)
                        cancellationToken.ThrowIfCancellationRequested();

                    var lastValue = db.QueryFirstOrDefault<T>(sql);
                    if ((expected == null && lastValue == null) || expected?.Equals(lastValue) == true)
                        return;

                    Thread.Sleep(50);
                }
            }
        }

#pragma warning disable CA1816
        public void Dispose()
#pragma warning restore CA1816
        {
            cancellationSource.Cancel();
        }
    }
}
