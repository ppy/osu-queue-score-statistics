// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using Dapper;
using Dapper.Contrib.Extensions;
using Newtonsoft.Json;
using osu.Game.Rulesets.Scoring;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table("solo_scores")]
    public class SoloScore
    {
        public long id { get; set; }
        public int user_id { get; set; }
        public int beatmap_id { get; set; }
        public int ruleset_id { get; set; }
        public bool passed { get; set; }

        public Dictionary<HitResult, int> statistics { get; set; } = new Dictionary<HitResult, int>();
    }

    internal class StatisticsTypeHandler : SqlMapper.TypeHandler<Dictionary<HitResult, int>>
    {
        public override Dictionary<HitResult, int> Parse(object value)
        {
            return JsonConvert.DeserializeObject<Dictionary<HitResult, int>>(value.ToString());
        }

        public override void SetValue(IDbDataParameter parameter, Dictionary<HitResult, int> value)
        {
            parameter.Value = value == null ? DBNull.Value : JsonConvert.SerializeObject(value);
            parameter.DbType = DbType.String;
        }
    }
}
