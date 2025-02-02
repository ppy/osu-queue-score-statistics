// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table("daily_challenge_user_stats")]
    public record DailyChallengeUserStats
    {
        public uint daily_streak_current { get; set; }
        public uint daily_streak_best { get; set; }
        public uint playcount { get; set; }
    }
}
