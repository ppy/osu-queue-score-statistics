// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance.Scores;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance
{
    [Command("scores", Description = "Updates individual score PP values.")]
    [Subcommand(typeof(UpdateAllScoresByUserCommand))]
    [Subcommand(typeof(UpdateScoresFromListCommand))]
    [Subcommand(typeof(UpdateScoresFromSqlCommand))]
    [Subcommand(typeof(UpdateScoresForUsersCommand))]
    public sealed class UpdateScoresCommands
    {
        public Task<int> OnExecuteAsync(CommandLineApplication app, CancellationToken cancellationToken)
        {
            app.ShowHelp(false);
            return Task.FromResult(1);
        }
    }
}
