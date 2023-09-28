// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;
using osu.Game.Rulesets.Scoring.Legacy;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table("osu_beatmap_scoring_attribs")]
    public class BeatmapScoringAttributes
    {
        [ExplicitKey]
        public uint beatmap_id { get; set; }

        public ushort mode { get; set; }

        public int legacy_accuracy_score { get; set; }

        public long legacy_combo_score { get; set; }

        public float legacy_bonus_score_ratio { get; set; }

        public LegacyScoreAttributes ToAttributes() => new LegacyScoreAttributes
        {
            AccuracyScore = legacy_accuracy_score,
            ComboScore = legacy_combo_score,
            BonusScoreRatio = legacy_bonus_score_ratio
        };
    }
}
