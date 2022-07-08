// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance.UserTotals
{
    [Command("list", Description = "Updates the total PP of specific users.")]
    public class UpdateUserTotalsForUsers : PerformanceCommand
    {
        [UsedImplicitly]
        [Required]
        [Argument(0, Description = "A comma-separated list of users to compute PP for.")]
        public string UsersString { get; set; } = string.Empty;

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            int[] userIds = ParseIntIds(UsersString);
            await ProcessUserTotals(userIds, cancellationToken);
            return 0;
        }
    }
}
