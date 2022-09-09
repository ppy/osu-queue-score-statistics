// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    public class ScoreItem : QueueItem
    {
        public ProcessHistory? ProcessHistory;

        public SoloScore Score;

        public ScoreItem(SoloScore score, ProcessHistory? history = null)
        {
            Score = score;
            ProcessHistory = history;
        }

        public void MarkProcessed() =>
            ProcessHistory = new ProcessHistory
            {
                score_id = (long)Score.id,
                processed_version = ScoreStatisticsProcessor.VERSION
            };

        public override string ToString() => Score.ToString();
    }
}
