// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// Thrown by <see cref="IProcessor"/>s when the score's processing should be aborted immediately.
    /// All side effects from the score processing should either be rolled back or not allowed to happen.
    /// </summary>
    public class ProcessingAbortedException : Exception
    {
        public ProcessingAbortedException(string message)
            : base(message)
        {
        }
    }
}
