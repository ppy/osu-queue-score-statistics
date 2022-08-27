// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;
using Newtonsoft.Json;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table(TABLE_NAME)]
    public class SoloScore
    {
        public const string TABLE_NAME = "solo_scores";

        [ExplicitKey]
        public long id { get; set; }

        public int user_id { get; set; }

        public int beatmap_id { get; set; }

        public int ruleset_id { get; set; }

        public string data
        {
            get => JsonConvert.SerializeObject(ScoreInfo);
            set
            {
                var scoreInfo = JsonConvert.DeserializeObject<SoloScoreInfo>(value);

                if (scoreInfo == null)
                    return;

                ScoreInfo = scoreInfo;

                ScoreInfo.id = id;
                ScoreInfo.beatmap_id = beatmap_id;
                ScoreInfo.user_id = user_id;
                ScoreInfo.ruleset_id = ruleset_id;
            }
        }

        [JsonIgnore]
        public bool preserve { get; set; }

        [JsonIgnore]
        public SoloScoreInfo ScoreInfo = new SoloScoreInfo();

        [JsonIgnore]
        public DateTimeOffset created_at { get; set; }

        [JsonIgnore]
        public DateTimeOffset updated_at { get; set; }

        public override string ToString() => $"score_id: {id} user_id: {user_id}";
    }
}
