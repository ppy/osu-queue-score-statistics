// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;
using osu.Game.Beatmaps;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table("osu_beatmapsets")]
    public class BeatmapSet
    {
        [ExplicitKey]
        public int beatmapset_id { get; set; }

        public uint user_id { get; set; }

        public string artist { get; set; } = string.Empty;
        public string title { get; set; } = string.Empty;
        public string creator { get; set; } = string.Empty;
        public double bpm { get; set; }

        public BeatmapOnlineStatus approved { get; set; }
        public DateTimeOffset? approved_date { get; set; }
    }
}
