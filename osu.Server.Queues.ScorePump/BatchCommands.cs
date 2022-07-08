// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScorePump.Performance;

namespace osu.Server.Queues.ScorePump
{
    [Command(Name = "batch", Description = "Runs batch processing on pp scores / user totals.")]
    [Subcommand(typeof(UpdateScoresCommands))]
    [Subcommand(typeof(UpdateUserTotalsCommands))]
    public sealed class BatchCommands
    {
        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp(false);
            return 1;
        }
    }
}
