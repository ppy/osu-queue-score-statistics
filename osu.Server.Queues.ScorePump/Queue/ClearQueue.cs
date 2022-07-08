// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump.Queue
{
    [Command("clear-queue", Description = "Completely empties the processing queue")]
    public class ClearQueue : QueueCommand
    {
        public int OnExecute(CancellationToken cancellationToken)
        {
            Queue.ClearQueue();
            Console.WriteLine("Queue has been cleared!");
            return 0;
        }
    }
}
