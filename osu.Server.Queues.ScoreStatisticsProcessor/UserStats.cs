// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics.CodeAnalysis;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public class UserStats
    {
        public long user_id { get; set; }
        public int playcount { get; set; } // only used for initial population
        public int pos { get; set; }
        public double pp { get; set; }
        public double accuracy { get; set; }
        public int x_rank_count { get; set; }
        public int xh_rank_count { get; set; }
        public int s_rank_count { get; set; }
        public int sh_rank_count { get; set; }
        public int a_rank_count { get; set; }
    }
}
