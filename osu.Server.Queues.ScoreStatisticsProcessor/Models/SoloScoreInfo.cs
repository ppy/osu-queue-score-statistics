// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using osu.Game.Online.API;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    public class SoloScoreInfo // TODO: hopefully combine with client-side ScoreInfo class.
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
}
