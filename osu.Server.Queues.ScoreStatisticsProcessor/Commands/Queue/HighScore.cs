// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    public class HighScore
    {
        public ulong score_id { get; set; }
        public int beatmap_id { get; set; }
        public int user_id { get; set; }
        public int score { get; set; }
        public ushort maxcombo { get; set; }
        public string rank { get; set; } = null!; // Actually a ScoreRank, but reading as a string for manual parsing.
        public ushort count50 { get; set; }
        public ushort count100 { get; set; }
        public ushort count300 { get; set; }
        public ushort countmiss { get; set; }
        public ushort countgeki { get; set; }
        public ushort countkatu { get; set; }
        public bool perfect { get; set; }
        public int enabled_mods { get; set; }
        public DateTimeOffset date { get; set; }
        public float? pp { get; set; }
        public bool replay { get; set; }
        public bool hidden { get; set; }
        public string country_acronym { get; set; } = null!;

        // These come from score_process_queue. Used in join context.
        public uint? queue_id { get; set; }
        public byte? status { get; set; }

        // ID of this score in the `scores` table. Used in join context.
        public ulong? new_id { get; set; }

        // These come from osu_scores. If present, this is a non-high-score, ie. is sourced from the osu_scores table series.
        public byte[]? scorechecksum { get; set; }
        public bool pass { get; set; } = true; // defaults true since osu_scores_high does not have this column (all scores are pass).

        public bool ShouldPreserve => scorechecksum == null;
    }
}
