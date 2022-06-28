// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance.Totals
{
    [Command("update-totals", Description = "Updates the total PP of users.")]
    [Subcommand(typeof(UpdateAllTotalsCommand))]
    [Subcommand(typeof(UpdateUsersTotalsCommand))]
    public sealed class UpdateTotalsMain
    {
        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp(false);
            return 1;
        }
    }
}
