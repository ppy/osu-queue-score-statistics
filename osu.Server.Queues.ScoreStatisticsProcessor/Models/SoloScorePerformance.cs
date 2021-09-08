// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table("solo_scores_v2_performance")] // TODO: change name after osu-web has updated to this format.
    public class SoloScorePerformance
    {
        [ExplicitKey]
        public ulong score_id { get; set; }

        public float? pp { get; set; }
    }
}
