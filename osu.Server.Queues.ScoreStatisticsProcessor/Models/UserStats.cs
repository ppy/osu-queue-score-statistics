// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    public abstract class UserStats
    {
        [ExplicitKey]
        public int user_id { get; set; }

        public int count300 { get; set; }
        public int count100 { get; set; }
        public int count50 { get; set; }
        public int countMiss { get; set; }
        public long accuracy_total { get; set; }
        public long accuracy_count { get; set; }
        public float accuracy { get; set; }
        public int playcount { get; set; }
        public long ranked_score { get; set; }
        public long total_score { get; set; }
        public int x_rank_count { get; set; }
        public int xh_rank_count { get; set; }
        public int s_rank_count { get; set; }
        public int sh_rank_count { get; set; }
        public int a_rank_count { get; set; }
        public int rank { get; set; }
        public float level { get; set; }
        public int replay_popularity { get; set; }
        public int fail_count { get; set; }
        public int exit_count { get; set; }
        public ushort max_combo { get; set; }
        public string country_acronym { get; set; } = "XX";
        public float rank_score { get; set; }
        public int rank_score_index { get; set; }
        public float accuracy_new { get; set; }
        public DateTimeOffset last_update { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset last_played { get; set; } = DateTimeOffset.Now;
        public long total_seconds_played { get; set; }
    }
}
