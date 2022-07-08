// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScorePump.Queue;

namespace osu.Server.Queues.ScorePump;

[Command(Name = "queue", Description = "Perform various operations on the processing queue.")]
[Subcommand(typeof(PumpTestData))]
[Subcommand(typeof(PumpAllScores))]
[Subcommand(typeof(WatchNewScores))]
[Subcommand(typeof(ClearQueue))]
[Subcommand(typeof(ImportHighScores))]
public sealed class QueueCommands
{
    public int OnExecute(CommandLineApplication app)
    {
        app.ShowHelp(false);
        return 1;
    }
}
