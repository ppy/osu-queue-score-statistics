// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using JetBrains.Annotations;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance.Scores
{
    [Command(Name = "sql", Description = "Computes pp of all scores of users given by an SQL select statement.")]
    public class UpdateScoresFromSql : PerformanceCommand
    {
        [UsedImplicitly]
        [Required]
        [Argument(0, Description = "The SQL statement selecting the user ids to compute.")]
        public string Statement { get; set; } = string.Empty;

        protected override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
        {
            int[] userIds;

            using (var db = Queue.GetDatabaseConnection())
                userIds = (await db.QueryAsync<int>(Statement)).ToArray();

            await ProcessUserScores(userIds, cancellationToken);
            return 0;
        }
    }
}
