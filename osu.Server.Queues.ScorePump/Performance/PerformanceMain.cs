// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScorePump.Performance.Totals;

namespace osu.Server.Queues.ScorePump.Performance
{
    [Command(Name = "performance", Description = "Computes the performance (pp) of scores.")]
    [Subcommand(typeof(AllCommand))]
    [Subcommand(typeof(SqlCommand))]
    [Subcommand(typeof(UsersCommand))]
    [Subcommand(typeof(ScoresCommand))]
    [Subcommand(typeof(UpdateTotalsMain))]
    public sealed class PerformanceMain
    {
        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp(false);
            return 1;
        }
    }
}
