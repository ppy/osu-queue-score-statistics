// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance
{
    public abstract class PerformanceCommand : ScorePump
    {
        protected PerformanceProcessor Processor { get; private set; } = null!;

        [Option(Description = "Number of threads to use.")]
        public int Threads { get; set; } = 1;

        public virtual async Task<int> OnExecuteAsync(CommandLineApplication app)
        {
            Processor = await PerformanceProcessor.CreateAsync(() => Queue.GetDatabaseConnection());
            return await ExecuteAsync(app);
        }

        protected abstract Task<int> ExecuteAsync(CommandLineApplication app);
    }
}
