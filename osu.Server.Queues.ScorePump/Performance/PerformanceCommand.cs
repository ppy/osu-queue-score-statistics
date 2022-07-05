// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance
{
    public abstract class PerformanceCommand : ScorePump
    {
        protected PerformanceProcessor Processor { get; private set; } = null!;

        [Option(Description = "Number of threads to use.")]
        public int Threads { get; set; } = 1;

        public virtual async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            using (var db = Queue.GetDatabaseConnection())
                Processor = await PerformanceProcessor.CreateAsync(db);
            return await ExecuteAsync(cancellationToken);
        }

        protected abstract Task<int> ExecuteAsync(CancellationToken cancellationToken);

        protected async Task ProcessPartitioned<T>(IEnumerable<T> values, Func<T, Task> processFunc, CancellationToken cancellationToken)
        {
            await Task.WhenAll(Partitioner
                               .Create(values)
                               .GetPartitions(Threads)
                               .AsParallel()
                               .Select(processPartition));

            async Task processPartition(IEnumerator<T> partition)
            {
                using (partition)
                {
                    while (partition.MoveNext())
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        await processFunc(partition.Current);
                    }
                }
            }
        }
    }
}
