// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance
{
    [Command("scores", Description = "Computes pp of specific scores.")]
    public class ScoresCommand : PerformanceCommand
    {
    }
}
