// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance
{
    [Command(Name = "sql", Description = "Computes pp of users given by an SQL select statement.")]
    public class SqlCommand : PerformanceCommand
    {
        [Argument(0, Description = "The SQL statement selecting the user ids to compute.")]
        public string Statement { get; set; } = null!;
    }
}
