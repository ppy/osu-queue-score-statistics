// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    public class ScoreItem : QueueItem
    {
        [CanBeNull]
        public ProcessHistory ProcessHistory;

        public SoloScore Score;

        public void MarkProcessed() =>
            ProcessHistory = new ProcessHistory
            {
                id = Score.id,
                processed_version = ScoreStatisticsProcessor.VERSION
            };

        public override string ToString() => Score.ToString();
    }
}
