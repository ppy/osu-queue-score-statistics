// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using JetBrains.Annotations;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    public class ScoreItem : QueueItem
    {
        [CanBeNull]
        public ProcessHistory ProcessHistory;

        public SoloScore Score;

        public override string ToString() => $"score_id: {Score.id} user_id: {Score.user_id}";

        public void MarkProcessed()
        {
            ProcessHistory = new ProcessHistory(Score.id, ScoreStatisticsProcessor.VERSION);
        }
    }
}
