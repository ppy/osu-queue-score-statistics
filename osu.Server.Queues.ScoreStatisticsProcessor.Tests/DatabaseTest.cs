// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Dapper.Contrib.Extensions;
using osu.Framework.Extensions.ExceptionExtensions;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;
using Xunit.Sdk;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    [Collection("Database tests")] // Ensure all tests hitting the database are run sequentially (no parallel execution).
    public abstract class DatabaseTest : IDisposable
    {
        protected readonly ScoreStatisticsProcessor Processor;

        protected CancellationToken CancellationToken => cancellationSource.Token;

        protected const int MAX_COMBO = 1337;

        protected const int TEST_BEATMAP_ID = 1;
        protected const int TEST_BEATMAP_SET_ID = 1;

        private readonly CancellationTokenSource cancellationSource = new CancellationTokenSource(10000);

        private Exception? firstError;

        protected DatabaseTest()
        {
            Processor = new ScoreStatisticsProcessor();
            Processor.Error += processorOnError;

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
                db.Execute($"TRUNCATE TABLE {Beatmap.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {BeatmapSet.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {SoloScore.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {ProcessHistory.TABLE_NAME}");
                db.Execute($"TRUNCATE TABLE {SoloScorePerformance.TABLE_NAME}");

                AddBeatmap();
            }

            Task.Run(() => Processor.Run(CancellationToken), CancellationToken);
        }

        private static ulong scoreIDSource;

        protected void PushToQueueAndWaitForProcess(ScoreItem item)
        {
            // To keep the flow of tests simple, require single-file addition of items.
            if (Processor.GetQueueSize() > 0)
                throw new InvalidOperationException("Queue was still processing an item when attempting to push another one.");

            long processedBefore = Processor.TotalProcessed;

            Processor.PushToQueue(item);

            WaitForDatabaseState($"SELECT score_id FROM {ProcessHistory.TABLE_NAME} WHERE score_id = {item.Score.id}", item.Score.id, CancellationToken);
            WaitForTotalProcessed(processedBefore + 1, CancellationToken);
        }

        public static ScoreItem CreateTestScore(int? rulesetId = null, int? beatmapId = null)
        {
            var row = new SoloScore
            {
                id = Interlocked.Increment(ref scoreIDSource),
                user_id = 2,
                beatmap_id = beatmapId ?? TEST_BEATMAP_ID,
                ruleset_id = rulesetId ?? 0,
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

        protected void IgnoreProcessorExceptions()
        {
            Processor.Error -= processorOnError;
        }

        /// <summary>
        /// All beatmaps which where added in this test via <see cref="AddBeatmap"/>.
        /// </summary>
        protected IReadOnlyList<Beatmap> AllBeatmaps => beatmaps;

        private readonly List<Beatmap> beatmaps = new List<Beatmap>();

        protected void AddBeatmap(Action<Beatmap>? beatmapSetup = null, Action<BeatmapSet>? beatmapSetSetup = null)
        {
            var beatmap = new Beatmap { approved = BeatmapOnlineStatus.Ranked };
            var beatmapSet = new BeatmapSet { approved = BeatmapOnlineStatus.Ranked };

            beatmapSetup?.Invoke(beatmap);
            beatmapSetSetup?.Invoke(beatmapSet);

            if (beatmap.beatmap_id == 0) beatmap.beatmap_id = TEST_BEATMAP_ID;
            if (beatmapSet.beatmapset_id == 0) beatmapSet.beatmapset_id = TEST_BEATMAP_SET_ID;

            if (beatmap.beatmapset_id > 0 && beatmap.beatmapset_id != beatmapSet.beatmapset_id)
                throw new ArgumentException($"{nameof(beatmapSetup)} method specified different {nameof(beatmap.beatmapset_id)} from the one specified in the {nameof(beatmapSetSetup)} method.");

            // Copy over set ID for cases where the setup steps only set it on the beatmapSet.
            beatmap.beatmapset_id = beatmapSet.beatmapset_id;

            using (var db = Processor.GetDatabaseConnection())
            {
                try
                {
                    db.Insert(beatmap);
                    db.Insert(beatmapSet);
                }
                catch
                {
                    db.Update(beatmap);
                    db.Update(beatmapSet);
                    beatmaps.RemoveAll(b => b.beatmap_id == beatmap.beatmap_id);
                }

                beatmaps.Add(beatmap);
            }
        }

        protected void WaitForTotalProcessed(long count, CancellationToken cancellationToken)
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
                T? lastValue = default;

                while (true)
                {
                    if (!Debugger.IsAttached)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw new TimeoutException($"Waiting for database state took too long (expected: {expected} last: {lastValue} sql: {sql})");
                    }

                    lastValue = db.QueryFirstOrDefault<T>(sql);

                    if ((expected == null && lastValue == null) || expected?.Equals(lastValue) == true)
                        return;

                    firstError?.Rethrow();

                    Thread.Sleep(50);
                }
            }
        }

        private void processorOnError(Exception? exception, ScoreItem _) => firstError ??= exception;

#pragma warning disable CA1816
        public virtual void Dispose()
#pragma warning restore CA1816
        {
            cancellationSource.Cancel();
        }
    }
}
