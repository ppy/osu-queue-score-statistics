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
    [Table("osu_beatmaps")]
    public class Beatmap
    {
        [ExplicitKey]
        public uint beatmap_id { get; set; }

        public ushort countTotal { get; set; }
        public ushort countNormal { get; set; }
        public ushort countSlider { get; set; }
        public ushort countSpinner { get; set; }
        public byte playmode { get; set; }
        public float difficultyrating { get; set; }
        public BeatmapOnlineStatus approved { get; set; }
    }
}
