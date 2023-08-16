// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance.UserTotals;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Performance
{
    [Command("user-totals", Description = "Updates user total PP values.")]
    [Subcommand(typeof(UpdateAllUserTotalsCommand))]
    [Subcommand(typeof(UpdateUserTotalsForUsersCommand))]
    public sealed class UpdateUserTotalsCommands
    {
        public Task<int> OnExecuteAsync(CommandLineApplication app, CancellationToken cancellationToken)
        {
            app.ShowHelp(false);
            return Task.FromResult(1);
        }
    }
}
