// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    public class ScoreItem : QueueItem
    {
        // TODO: these may be best moved to their own table / storage
        public DateTimeOffset? processed_at { get; set; }
        public byte? processed_version { get; set; }

        public SoloScore Score;

        public override string ToString() => $"score_id: {Score.id} user_id: {Score.user_id}";
    }
}
