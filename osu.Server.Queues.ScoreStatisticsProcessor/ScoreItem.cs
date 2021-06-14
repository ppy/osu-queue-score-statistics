// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics.CodeAnalysis;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public class ScoreItem : QueueItem
    {
        public long score_id { get; set; }
        public long user_id { get; set; }
        public long beatmap_id { get; set; }
        public bool passed { get; set; }

        public override string ToString() => $"score_id: {score_id} user_id: {user_id}";
    }
}
