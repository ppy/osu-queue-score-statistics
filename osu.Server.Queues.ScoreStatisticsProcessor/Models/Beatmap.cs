// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets.Scoring.Legacy;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table("osu_beatmaps")]
    public class Beatmap : IBeatmapOnlineInfo
    {
        [ExplicitKey]
        public uint beatmap_id { get; set; }

        public uint beatmapset_id { get; set; }

        public uint user_id { get; set; }

        public uint countTotal { get; set; }
        public uint countNormal { get; set; }
        public uint countSlider { get; set; }
        public uint countSpinner { get; set; }
        public int total_length { get; set; }
        public float diff_drain { get; set; }
        public float diff_size { get; set; }
        public float diff_overall { get; set; }
        public float diff_approach { get; set; }
        public byte playmode { get; set; }
        public int playcount { get; set; }
        public BeatmapOnlineStatus approved { get; set; }
        public float difficultyrating { get; set; }
        public uint hit_length { get; set; }
        public float bpm { get; set; }

        [Computed]
        public BeatmapSet? beatmapset { get; set; }

        public LegacyBeatmapConversionDifficultyInfo GetLegacyBeatmapConversionDifficultyInfo() => new LegacyBeatmapConversionDifficultyInfo
        {
            SourceRuleset = new APIBeatmap.APIRuleset { OnlineID = playmode },
            DrainRate = diff_drain,
            ApproachRate = diff_approach,
            CircleSize = diff_size,
            OverallDifficulty = diff_overall,
            EndTimeObjectCount = (int)(countSlider + countSpinner),
            TotalObjectCount = (int)countTotal,
        };

        #region IBeatmapOnlineInfo

        float IBeatmapOnlineInfo.ApproachRate => diff_approach;
        float IBeatmapOnlineInfo.CircleSize => diff_size;
        float IBeatmapOnlineInfo.DrainRate => diff_drain;
        float IBeatmapOnlineInfo.OverallDifficulty => diff_overall;
        int IBeatmapOnlineInfo.CircleCount => (int)countNormal;
        int IBeatmapOnlineInfo.SliderCount => (int)countSlider;
        int IBeatmapOnlineInfo.SpinnerCount => (int)countSpinner;
        double IBeatmapOnlineInfo.HitLength => hit_length;
        int IBeatmapOnlineInfo.PlayCount => playcount;

        int? IBeatmapOnlineInfo.MaxCombo => throw new NotSupportedException();
        int IBeatmapOnlineInfo.PassCount => throw new NotSupportedException();
        APIFailTimes IBeatmapOnlineInfo.FailTimes => throw new NotSupportedException();

        #endregion
    }
}
