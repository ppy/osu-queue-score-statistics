// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class SlaveLatencyChecker
    {
        /// <summary>
        /// The latency a slave is allowed to fall behind before we start to panic.
        /// </summary>
        private static readonly int maximum_slave_latency_seconds = int.Parse(Environment.GetEnvironmentVariable("MAX_SLAVE_LATENCY") ?? "120");

        private static long lastLatencyCheckTimestamp;

        public static async Task CheckSlaveLatency(MySqlConnection db, CancellationToken cancellationToken)
        {
            long currentTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();

            if (currentTimestamp - lastLatencyCheckTimestamp < Math.Max(1, maximum_slave_latency_seconds / 2))
                return;

            lastLatencyCheckTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();

            int? latency;

            do
            {
                // This latency is best-effort, and randomly queried from available hosts (with rough precedence of the importance of the host).
                // When we detect a high latency value, a recovery period should be introduced where we are pretty sure that we're back in a good
                // state before resuming operations.
                latency = db.QueryFirstOrDefault<int?>("SELECT `count` FROM `osu_counts` WHERE NAME = 'slave_latency'");

                if (latency == null || latency < maximum_slave_latency_seconds)
                    return;

                Console.WriteLine($"Current slave latency of {latency} seconds exceeded maximum of {maximum_slave_latency_seconds} seconds.");
                Console.WriteLine("Sleeping to allow catch-up.");

                await Task.Delay(maximum_slave_latency_seconds * 1000, cancellationToken);
            } while (latency > maximum_slave_latency_seconds);
        }
    }
}
