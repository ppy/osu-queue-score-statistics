// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table(TABLE_NAME)]
    public class Beatmap
    {
        public const string TABLE_NAME = "osu_beatmaps";

        [ExplicitKey]
        public uint beatmap_id { get; set; }

        public ushort countTotal { get; set; }
        public ushort countNormal { get; set; }
        public ushort countSlider { get; set; }
        public ushort countSpinner { get; set; }
        public float diff_drain { get; set; }
        public float diff_size { get; set; }
        public float diff_overall { get; set; }
        public float diff_approach { get; set; }
        public byte playmode { get; set; }
        public BeatmapOnlineStatus approved { get; set; }
        public float difficultyrating { get; set; }

        public APIBeatmap ToAPIBeatmap() => new APIBeatmap
        {
            OnlineID = (int)beatmap_id,
            CircleCount = countNormal,
            SliderCount = countSlider,
            SpinnerCount = countSpinner,
            DrainRate = diff_drain,
            CircleSize = diff_size,
            OverallDifficulty = diff_overall,
            ApproachRate = diff_approach,
            RulesetID = playmode,
            StarRating = difficultyrating
        };
    }
}
