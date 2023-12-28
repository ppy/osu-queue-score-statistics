// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    public class ScoreProcessQueue
    {
        public uint queue_id { get; set; }
        public ulong score_id { get; set; }
        public byte status { get; set; }
        public bool is_deletion { get; set; }
    }
}
