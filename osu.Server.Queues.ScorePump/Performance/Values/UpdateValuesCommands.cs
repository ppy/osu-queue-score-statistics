// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance.Values
{
    [Command("values", Description = "Updates score PP values.")]
    [Subcommand(typeof(UpdateValuesAllCommand))]
    [Subcommand(typeof(UpdateValuesScoresCommand))]
    [Subcommand(typeof(UpdateValuesSqlCommand))]
    [Subcommand(typeof(UpdateValuesUsersCommand))]
    public sealed class UpdateValuesCommands
    {
        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp(false);
            return 1;
        }
    }
}
