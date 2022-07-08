// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScorePump.Performance.Totals;
using osu.Server.Queues.ScorePump.Performance.Values;

namespace osu.Server.Queues.ScorePump.Performance
{
    [Command(Name = "update-pp", Description = "Computes the performance (pp) of scores.")]
    [Subcommand(typeof(UpdateScoresCommands))]
    [Subcommand(typeof(UpdateUserTotalsCommands))]
    public sealed class PerformanceCommands
    {
        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp(false);
            return 1;
        }
    }
}
