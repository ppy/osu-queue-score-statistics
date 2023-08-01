// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump
{
    [Command("recalculate-scores", Description = "Process all scores in the `solo_scores` table, recalculating and writing any changes in total score and accuracy values.")]
    public class RecalculateScoresCommand : BaseCommand
    {
        private const int batch_size = 10000;

        /// <summary>
        /// The score ID to start the process from. This can be used to resume an existing job.
        /// </summary>
        [Option(CommandOptionType.SingleValue)]
        public ulong StartId { get; set; }

        /// <summary>
        /// The amount of time to sleep between score batches.
        /// </summary>
        [Option(CommandOptionType.SingleValue)]
        public int Delay { get; set; }

        public Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            // TODO: the logic to actually recalculate scores was removed. should be considered before this command is used.
            // see https://github.com/ppy/osu-queue-score-statistics/pull/135.
            throw new NotImplementedException();
        }
    }
}
