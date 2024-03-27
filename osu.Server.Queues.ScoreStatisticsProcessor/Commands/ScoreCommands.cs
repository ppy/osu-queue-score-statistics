// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Score;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands
{
    [Command(Name = "score", Description = "Runs batch processing on score totals for scores and users.")]
    [Subcommand(typeof(ChangeModMultiplierCommand))]
    public class ScoreCommands
    {
        [UsedImplicitly]
        public Task<int> OnExecuteAsync(CommandLineApplication app, CancellationToken _)
        {
            app.ShowHelp(false);
            return Task.FromResult(1);
        }
    }
}
