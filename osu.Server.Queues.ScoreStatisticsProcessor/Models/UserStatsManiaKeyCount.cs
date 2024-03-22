// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    /// <summary>
    /// Base class for mania keycount-specific user stats.
    /// Very close resemblance to <see cref="UserStats"/>, but the mania keycount tables have less columns.
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    public class UserStatsManiaKeyCount
    {
        [ExplicitKey]
        public uint user_id { get; set; }

        public int playcount { get; set; }

        public int x_rank_count { get; set; }
        public int xh_rank_count { get; set; }
        public int s_rank_count { get; set; }
        public int sh_rank_count { get; set; }
        public int a_rank_count { get; set; }

        public string country_acronym { get; set; } = "XX";

        public float rank_score { get; set; }
        public int rank_score_index { get; set; }

        public float accuracy_new { get; set; }
        public DateTimeOffset last_update { get; set; } = DateTimeOffset.Now;
        public DateTimeOffset last_played { get; set; } = DateTimeOffset.Now;
    }
}
