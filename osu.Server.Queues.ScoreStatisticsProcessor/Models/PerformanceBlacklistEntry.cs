// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table("osu_beatmap_performance_blacklist")]
    public class PerformanceBlacklistEntry
    {
        public uint beatmap_id { get; set; }
        public uint mode { get; set; }
    }
}
