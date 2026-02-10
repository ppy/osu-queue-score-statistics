// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Framework.Extensions.ExceptionExtensions;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch.Difficulty;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mania.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Difficulty;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Taiko.Difficulty;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;
using Xunit;
using Xunit.Sdk;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    [Collection("Database tests")] // Ensure all tests hitting the database are run sequentially (no parallel execution).
    public abstract class DatabaseTest : IDisposable
    {
        protected readonly ScoreStatisticsQueueProcessor Processor;

        protected CancellationToken CancellationToken => cancellationSource.Token;

        protected const int MAX_COMBO = 1337;

        protected const int TEST_BEATMAP_ID = 1;
        protected const int TEST_BEATMAP_SET_ID = 1;
        protected ushort TestBuildID;

        private readonly CancellationTokenSource cancellationSource;

        private Exception? firstError;

        protected DatabaseTest(AssemblyName[]? externalProcessorAssemblies = null)
        {
            cancellationSource = Debugger.IsAttached
                ? new CancellationTokenSource()
                : new CancellationTokenSource(20000);

            Environment.SetEnvironmentVariable("REALTIME_DIFFICULTY", "0");

            Processor = new ScoreStatisticsQueueProcessor(externalProcessorAssemblies: externalProcessorAssemblies);
            Processor.Error += processorOnError;

            Processor.ClearQueue();

            using (var db = Processor.GetDatabaseConnection())
            {
                // just a safety measure for now to ensure we don't hit production. since i was running on production until now.
                // will throw if not on test database.
                if (db.QueryFirstOrDefault<int?>("SELECT `count` FROM `osu_counts` WHERE `name` = 'is_production'") != null)
                    throw new InvalidOperationException("You are trying to do something very silly.");

                db.Execute("TRUNCATE TABLE osu_user_stats");
                db.Execute("TRUNCATE TABLE osu_user_stats_taiko");
                db.Execute("TRUNCATE TABLE osu_user_stats_fruits");
                db.Execute("TRUNCATE TABLE osu_user_stats_mania");
                db.Execute("TRUNCATE TABLE osu_user_beatmap_playcount");
                db.Execute("TRUNCATE TABLE osu_user_month_playcount");
                db.Execute("TRUNCATE TABLE osu_beatmaps");
                db.Execute("TRUNCATE TABLE osu_beatmapsets");
                db.Execute("TRUNCATE TABLE osu_beatmap_difficulty_attribs");

                // These tables are still views for now (todo osu-web plz).
                db.Execute("DELETE FROM scores");
                db.Execute("DELETE FROM score_process_history");

                db.Execute("TRUNCATE TABLE osu_builds");
                db.Execute("REPLACE INTO osu_counts (name, count) VALUES ('playcount', 0)");

                TestBuildID = db.QuerySingle<ushort>("INSERT INTO osu_builds (version, allow_performance) VALUES ('1.0.0', 1); SELECT LAST_INSERT_ID();");

                db.Execute("TRUNCATE TABLE `osu_user_performance_rank`");
                db.Execute("TRUNCATE TABLE `osu_user_performance_rank_highest`");
            }

            BeatmapStore.PurgeCaches();

            Task.Run(() => Processor.Run(CancellationToken), CancellationToken);
        }

        protected ScoreItem SetScoreForBeatmap(uint beatmapId, Action<ScoreItem>? scoreSetup = null)
        {
            using (MySqlConnection conn = Processor.GetDatabaseConnection())
            {
                var score = CreateTestScore(beatmapId: beatmapId);

                scoreSetup?.Invoke(score);

                InsertScore(conn, score);
                PushToQueueAndWaitForProcess(score);

                return score;
            }
        }

        protected static void InsertScore(MySqlConnection conn, ScoreItem score)
        {
            conn.Execute("INSERT INTO `scores` (`id`, `user_id`, `ruleset_id`, `beatmap_id`, `has_replay`, `preserve`, `ranked`, "
                         + "`rank`, `passed`, `accuracy`, `max_combo`, `total_score`, `data`, `pp`, `legacy_score_id`, `legacy_total_score`, "
                         + "`started_at`, `ended_at`, `build_id`) "
                         + "VALUES (@id, @user_id, @ruleset_id, @beatmap_id, @has_replay, @preserve, @ranked, "
                         + "@rank, @passed, @accuracy, @max_combo, @total_score, @data, @pp, @legacy_score_id, @legacy_total_score,"
                         + "@started_at, @ended_at, @build_id)",
                new
                {
                    score.Score.id,
                    score.Score.user_id,
                    score.Score.ruleset_id,
                    score.Score.beatmap_id,
                    score.Score.has_replay,
                    score.Score.preserve,
                    score.Score.ranked,
                    rank = score.Score.rank.ToString(),
                    score.Score.passed,
                    score.Score.accuracy,
                    score.Score.max_combo,
                    score.Score.total_score,
                    score.Score.data,
                    score.Score.pp,
                    score.Score.legacy_score_id,
                    score.Score.legacy_total_score,
                    score.Score.started_at,
                    score.Score.ended_at,
                    score.Score.build_id,
                });
        }

        private static ulong scoreIDSource;

        protected void PushToQueueAndWaitForProcess(ScoreItem item)
        {
            // To keep the flow of tests simple, require single-file addition of items.
            if (Processor.GetQueueSize() > 0)
                throw new InvalidOperationException("Queue was still processing an item when attempting to push another one.");

            long processedBefore = Processor.TotalProcessed;

            Processor.PushToQueue(item);

            WaitForDatabaseState($"SELECT score_id FROM score_process_history WHERE score_id = {item.Score.id}", item.Score.id, CancellationToken);
            WaitForTotalProcessed(processedBefore + 1, CancellationToken);
        }

        public static ScoreItem CreateTestScore(uint? rulesetId = null, uint? beatmapId = null)
        {
            var row = new SoloScore
            {
                id = Interlocked.Increment(ref scoreIDSource),
                user_id = 2,
                beatmap_id = beatmapId ?? TEST_BEATMAP_ID,
                ruleset_id = (ushort)(rulesetId ?? 0),
                started_at = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(180),
                ended_at = DateTimeOffset.UtcNow,
                max_combo = MAX_COMBO,
                total_score = 100000,
                rank = ScoreRank.S,
                passed = true,
                preserve = true,
                accuracy = 0.99,
            };

            var scoreData = new SoloScoreData
            {
                Statistics =
                {
                    { HitResult.Great, 5 },
                    { HitResult.LargeBonus, 0 }
                },
                MaximumStatistics =
                {
                    { HitResult.Great, 5 },
                    { HitResult.LargeBonus, 2 }
                },
            };

            row.ScoreData = scoreData;

            return new ScoreItem(row);
        }

        protected void IgnoreProcessorExceptions()
        {
            Processor.Error -= processorOnError;
        }

        protected Beatmap AddBeatmap(Action<Beatmap>? beatmapSetup = null, Action<BeatmapSet>? beatmapSetSetup = null)
        {
            var beatmap = new Beatmap { approved = BeatmapOnlineStatus.Ranked };
            var beatmapSet = new BeatmapSet { approved = BeatmapOnlineStatus.Ranked, approved_date = DateTimeOffset.Now.AddHours(-1) };

            beatmapSetup?.Invoke(beatmap);
            beatmapSetSetup?.Invoke(beatmapSet);

            if (beatmap.beatmap_id == 0) beatmap.beatmap_id = TEST_BEATMAP_ID;
            if (beatmapSet.beatmapset_id == 0) beatmapSet.beatmapset_id = TEST_BEATMAP_SET_ID;

            if (beatmap.beatmapset_id > 0 && beatmap.beatmapset_id != beatmapSet.beatmapset_id)
                throw new ArgumentException($"{nameof(beatmapSetup)} method specified different {nameof(beatmap.beatmapset_id)} from the one specified in the {nameof(beatmapSetSetup)} method.");

            // Copy over set ID for cases where the setup steps only set it on the beatmapSet.
            beatmap.beatmapset_id = (uint)beatmapSet.beatmapset_id;

            using (var db = Processor.GetDatabaseConnection())
            {
                db.Insert(beatmap);
                if (db.QuerySingleOrDefault<int>("SELECT COUNT(1) FROM `osu_beatmapsets` WHERE `beatmapset_id` = @beatmapSetId", new { beatmapSetId = beatmapSet.beatmapset_id }) == 0)
                    db.Insert(beatmapSet);
            }

            AddBeatmapAttributes<OsuDifficultyAttributes>(beatmap.beatmap_id, attr => attr.MaxCombo = MAX_COMBO);
            AddBeatmapAttributes<TaikoDifficultyAttributes>(beatmap.beatmap_id, attr => attr.MaxCombo = MAX_COMBO, mode: 1);
            AddBeatmapAttributes<CatchDifficultyAttributes>(beatmap.beatmap_id, attr => attr.MaxCombo = MAX_COMBO, mode: 2);
            AddBeatmapAttributes<ManiaDifficultyAttributes>(beatmap.beatmap_id, attr => attr.MaxCombo = MAX_COMBO, mode: 3);

            return beatmap;
        }

        protected void AddBeatmapAttributes<TDifficultyAttributes>(uint? beatmapId = null, Action<TDifficultyAttributes>? setup = null, ushort mode = 0, Mod[]? mods = null)
            where TDifficultyAttributes : DifficultyAttributes, new()
        {
            var attribs = new TDifficultyAttributes
            {
                StarRating = 5,
                MaxCombo = 5,
                Mods = mods ?? []
            };

            setup?.Invoke(attribs);

            var rulesetStore = new AssemblyRulesetStore();
            var rulesetInfo = rulesetStore.GetRuleset(mode)!;
            var ruleset = rulesetInfo.CreateInstance();

            beatmapId ??= TEST_BEATMAP_ID;
            uint modsInt = (uint)ruleset.ConvertToLegacyMods(attribs.Mods);

            using (var db = Processor.GetDatabaseConnection())
            {
                string attribsString = string.Join(", ", attribs.ToDatabaseAttributes().Select(a => $"({beatmapId.Value}, {mode}, {modsInt}, {a.attributeId}, {a.value})"));
                db.Execute($"INSERT INTO osu_beatmap_difficulty_attribs (beatmap_id, mode, mods, attrib_id, value) VALUES {attribsString} ON DUPLICATE KEY UPDATE value = VALUES(value)");
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

        protected void WaitForDatabaseState<T>(string sql, T expected, CancellationToken cancellationToken, object? param = null)
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

                    lastValue = db.QueryFirstOrDefault<T>(sql, param);

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
