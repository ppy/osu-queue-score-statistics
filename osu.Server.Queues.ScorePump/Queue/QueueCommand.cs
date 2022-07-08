// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace osu.Server.Queues.ScorePump.Queue
{
    public abstract class QueueCommand
    {
        protected readonly ScoreStatisticsProcessor.ScoreStatisticsProcessor Queue = new ScoreStatisticsProcessor.ScoreStatisticsProcessor();
    }
}
