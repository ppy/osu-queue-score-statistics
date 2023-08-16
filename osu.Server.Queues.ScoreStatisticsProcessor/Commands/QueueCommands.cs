// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands
{
    [Command(Name = "queue", Description = "Perform various operations on the processing queue.")]
    [Subcommand(typeof(PumpTestDataCommand))]
    [Subcommand(typeof(PumpAllScoresCommand))]
    [Subcommand(typeof(ClearQueueCommand))]
    [Subcommand(typeof(ImportHighScoresCommand))]
    public sealed class QueueCommands
    {
        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp(false);
            return 1;
        }
    }
}
