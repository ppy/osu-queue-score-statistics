// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScorePump.Performance.Scores;

namespace osu.Server.Queues.ScorePump.Performance
{
    [Command("scores", Description = "Updates individual score PP values.")]
    [Subcommand(typeof(UpdateAllScoresCommand))]
    [Subcommand(typeof(UpdateScoresFromListCommand))]
    [Subcommand(typeof(UpdateScoresFromSqlCommand))]
    [Subcommand(typeof(UpdateScoresForUsersCommand))]
    public sealed class UpdateScoresCommands
    {
        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp(false);
            return 1;
        }
    }
}
