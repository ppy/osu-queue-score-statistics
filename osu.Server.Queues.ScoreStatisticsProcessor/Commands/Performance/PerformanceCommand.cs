// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance
{
    public abstract class PerformanceCommand
    {
        protected ScorePerformanceProcessor ScoreProcessor { get; private set; } = null!;
        protected UserTotalPerformanceProcessor TotalProcessor { get; private set; } = null!;
        protected ManiaKeyModeUserStatsProcessor ManiaKeyModeProcessor { get; private set; } = null!;

        [Option(CommandOptionType.SingleValue, Template = "-r|--ruleset", Description = "The ruleset to process score for.")]
        public int RulesetId { get; set; }

        [Option(Description = "Number of threads to use.")]
        public int Threads { get; set; } = 1;

        [Option(CommandOptionType.SingleOrNoValue, Template = "-v|--verbose", Description = "Output verbose information on processing.")]
        public bool Verbose { get; set; }

        public virtual async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            ScoreProcessor = new ScorePerformanceProcessor { Verbose = Verbose };
            TotalProcessor = new UserTotalPerformanceProcessor();
            ManiaKeyModeProcessor = new ManiaKeyModeUserStatsProcessor();
            return await ExecuteAsync(cancellationToken);
        }

        protected abstract Task<int> ExecuteAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Parses a comma-separated list of IDs from a given input string.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <returns>The IDs.</returns>
        protected static uint[] ParseIntIds(string input) => input.Split(',').Select(uint.Parse).ToArray();

        /// <summary>
        /// Parses a comma-separated list of IDs from a given input string.
        /// </summary>
        /// <param name="input">The input string.</param>
        /// <returns>The IDs.</returns>
        protected static ulong[] ParseLongIds(string input) => input.Split(',').Select(ulong.Parse).ToArray();

        protected async Task ProcessUserTotals(uint[] userIds, CancellationToken cancellationToken)
        {
            if (userIds.Length == 0)
            {
                Console.WriteLine("No matching users to process!");
                return;
            }

            Console.WriteLine($"Processing user totals for {userIds.Length} users");

            int processedCount = 0;

            await ProcessPartitioned(userIds, async (db, transaction, userId) =>
            {
                var userStats = await DatabaseHelper.GetUserStatsAsync(userId, RulesetId, db, transaction);

                if (userStats == null)
                    return;

                // Mania per-key ranking statistic updates.
                if (RulesetId == 3)
                {
                    await updateKeyStats(4);
                    await updateKeyStats(7);

                    async Task updateKeyStats(int keyCount)
                    {
                        string keyCountTableName = $"osu_user_stats_mania_{keyCount}k";
                        var keyModeStats = db.QueryFirstOrDefault<UserStatsManiaKeyCount>($"SELECT * FROM {keyCountTableName} WHERE `user_id` = @user_id", userStats, transaction);

                        if (keyModeStats != null)
                        {
                            double rankScoreBefore = keyModeStats.rank_score;
                            double accBefore = keyModeStats.accuracy_new;

                            await ManiaKeyModeProcessor.UpdateUserStatsAsync(keyModeStats, keyCount, db, transaction, updateIndex: false);

                            if (Math.Abs(rankScoreBefore - keyModeStats.rank_score) > 0.1 ||
                                Math.Abs(accBefore - keyModeStats.accuracy_new) > 0.1)
                            {
                                await db.ExecuteAsync(
                                    $"UPDATE `{keyCountTableName}` "
                                    + $"SET `rank_score` = @rank_score, `playcount` = @playcount, `rank_score_index` = @rank_score_index, `accuracy_new` = @accuracy_new, "
                                    + $"`x_rank_count` = @x_rank_count, `xh_rank_count` = @xh_rank_count, `s_rank_count` = @s_rank_count, `sh_rank_count` = @sh_rank_count, `a_rank_count` = @a_rank_count "
                                    + $"WHERE `user_id` = @user_id", keyModeStats, transaction);
                            }
                        }
                    }
                }

                double rankScoreBefore = userStats.rank_score;
                double accBefore = userStats.accuracy_new;

                await TotalProcessor.UpdateUserStatsAsync(userStats, RulesetId, db, transaction);

                if (Math.Abs(rankScoreBefore - userStats.rank_score) > 0.1 ||
                    Math.Abs(accBefore - userStats.accuracy_new) > 0.1)
                {
                    await DatabaseHelper.UpdateUserStatsAsync(userStats, db, transaction);
                }

                if (Interlocked.Increment(ref processedCount) % 1000 == 0)
                    Console.WriteLine($"Processed {processedCount} of {userIds.Length}");
            }, cancellationToken);
        }

        protected async Task ProcessUserScores(uint[] userIds, CancellationToken cancellationToken)
        {
            if (userIds.Length == 0)
            {
                Console.WriteLine("No matching users to process!");
                return;
            }

            Console.WriteLine($"Processing user scores for {userIds.Length} users");

            int processedCount = 0;

            await ProcessPartitioned(userIds, async (conn, transaction, userId) =>
            {
                await ScoreProcessor.ProcessUserScoresAsync(userId, RulesetId, conn, transaction, cancellationToken: cancellationToken);

                Console.WriteLine($"Processed {Interlocked.Increment(ref processedCount)} of {userIds.Length}");
            }, cancellationToken);
        }

        protected async Task ProcessPartitioned<T>(IEnumerable<T> values, Func<MySqlConnection, MySqlTransaction, T, Task> processFunc, CancellationToken cancellationToken)
        {
            const int max_transaction_size = 50;

            await Task.WhenAll(Partitioner
                               .Create(values)
                               .GetPartitions(Threads)
                               .Select(processPartition));

            async Task processPartition(IEnumerator<T> partition)
            {
                MySqlTransaction? transaction = null;
                int transactionSize = 0;

                using (var connection = await DatabaseAccess.GetConnectionAsync(cancellationToken))
                using (partition)
                {
                    while (partition.MoveNext())
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        if (transaction == null)
                            await startTransaction(connection);

                        await processFunc(connection, transaction!, partition.Current);

                        if (++transactionSize >= max_transaction_size)
                            await commit();
                    }

                    await commit();
                }

                async Task commit()
                {
                    if (transaction == null)
                        return;

                    await transaction.CommitAsync(cancellationToken);
                    await transaction.DisposeAsync();

                    transaction = null;
                }

                async Task startTransaction(MySqlConnection connection)
                {
                    if (transaction != null)
                        throw new InvalidOperationException("Previous transaction was not committed");

                    transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadUncommitted, cancellationToken);
                    transactionSize = 0;
                }
            }
        }
    }
}
