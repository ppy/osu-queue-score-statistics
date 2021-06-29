// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using Dapper;
using Dapper.Contrib.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using osu.Game.IO.Serialization;
using osu.Game.Online.API;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table("solo_scores")]
    public class SoloScore : IJsonSerializable
    {
        public long id { get; set; }

        public int user_id { get; set; }

        public int beatmap_id { get; set; }

        public int ruleset_id { get; set; }

        public bool passed { get; set; }

        public int total_score { get; set; }

        public double accuracy { get; set; }

        // TODO: probably want to update this column to match user stats (short)?
        public int max_combo { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public ScoreRank rank { get; set; }

        public DateTimeOffset started_at { get; set; }

        public DateTimeOffset? ended_at { get; set; }

        public List<APIMod> mods { get; set; } = new List<APIMod>();

        public Dictionary<HitResult, int> statistics { get; set; } = new Dictionary<HitResult, int>();

        public override string ToString() => $"score_id: {id} user_id: {user_id}";

        [JsonIgnore]
        public DateTimeOffset created_at { get; set; }

        [JsonIgnore]
        public DateTimeOffset updated_at { get; set; }

        [JsonIgnore]
        public DateTimeOffset? deleted_at { get; set; }
    }

    internal class ModsTypeHandler : SqlMapper.TypeHandler<List<APIMod>>
    {
        public override List<APIMod> Parse(object? value)
        {
            return JsonConvert.DeserializeObject<List<APIMod>>(value?.ToString() ?? string.Empty) ?? new List<APIMod>();
        }

        public override void SetValue(IDbDataParameter parameter, List<APIMod>? value)
        {
            parameter.Value = value == null ? DBNull.Value : JsonConvert.SerializeObject(value);
            parameter.DbType = DbType.String;
        }
    }

    internal class StatisticsTypeHandler : SqlMapper.TypeHandler<Dictionary<HitResult, int>?>
    {
        public override Dictionary<HitResult, int> Parse(object? value)
        {
            return JsonConvert.DeserializeObject<Dictionary<HitResult, int>>(value?.ToString() ?? string.Empty) ?? new Dictionary<HitResult, int>();
        }

        public override void SetValue(IDbDataParameter parameter, Dictionary<HitResult, int>? value)
        {
            parameter.Value = value == null ? DBNull.Value : JsonConvert.SerializeObject(value);
            parameter.DbType = DbType.String;
        }
    }
}
