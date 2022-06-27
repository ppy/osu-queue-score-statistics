// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Performance
{
    [Command("users", Description = "Computes pp of specific users.")]
    public class UsersCommand : PerformanceCommand
    {
    }
}
