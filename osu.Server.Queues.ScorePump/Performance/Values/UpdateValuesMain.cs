// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance.Values
{
    [Command("values", Description = "Updates score PP values.")]
    [Subcommand(typeof(AllCommand))]
    [Subcommand(typeof(ScoresCommand))]
    [Subcommand(typeof(SqlCommand))]
    [Subcommand(typeof(UsersCommand))]
    public sealed class UpdateValuesMain
    {
        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp(false);
            return 1;
        }
    }
}
