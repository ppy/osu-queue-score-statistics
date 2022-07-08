// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScorePump.Performance.Scores;

namespace osu.Server.Queues.ScorePump.Performance
{
    [Command("scores", Description = "Updates individual score PP values.")]
    [Subcommand(typeof(UpdateAllScores))]
    [Subcommand(typeof(UpdateScoresFromList))]
    [Subcommand(typeof(UpdateScoresFromSql))]
    [Subcommand(typeof(UpdateScoresForUsers))]
    public sealed class UpdateScoresCommands
    {
        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp(false);
            return 1;
        }
    }
}
