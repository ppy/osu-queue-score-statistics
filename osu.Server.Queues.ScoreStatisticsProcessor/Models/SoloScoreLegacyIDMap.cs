// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table("score_legacy_id_map")]
    public class SoloScoreLegacyIDMap
    {
        [ExplicitKey]
        public ushort ruleset_id { get; set; }

        [ExplicitKey]
        public ulong old_score_id { get; set; }

        public ulong score_id { get; set; }
    }
}
