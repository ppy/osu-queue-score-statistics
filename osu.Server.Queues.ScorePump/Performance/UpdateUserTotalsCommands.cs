// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScorePump.Performance.UserTotals;

namespace osu.Server.Queues.ScorePump.Performance
{
    [Command("user-totals", Description = "Updates user total PP values.")]
    [Subcommand(typeof(UpdateAllUserTotals))]
    [Subcommand(typeof(UpdateUserTotalsForUsers))]
    public sealed class UpdateUserTotalsCommands
    {
        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp(false);
            return 1;
        }
    }
}
