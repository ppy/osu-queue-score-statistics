// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Dapper.Contrib.Extensions;
using Newtonsoft.Json;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table("scores")]
    public class SoloScore
    {
        [ExplicitKey]
        public ulong id { get; set; }

        public uint user_id { get; set; }

        public uint beatmap_id { get; set; }

        public ushort ruleset_id { get; set; }

        public bool has_replay { get; set; }
        public bool preserve { get; set; }
        public bool ranked { get; set; } = true;

        public ScoreRank rank { get; set; }

        public bool passed { get; set; } = true;

        public float accuracy { get; set; }

        public uint max_combo { get; set; }

        public uint total_score { get; set; }

        public SoloScoreData ScoreData = new SoloScoreData();

        public string data
        {
            get => JsonConvert.SerializeObject(ScoreData);
            set
            {
                var soloScoreData = JsonConvert.DeserializeObject<SoloScoreData>(value);
                if (soloScoreData != null)
                    ScoreData = soloScoreData;
            }
        }

        public double? pp { get; set; }

        public ulong? legacy_score_id { get; set; }
        public uint legacy_total_score { get; set; }

        public DateTimeOffset? started_at { get; set; }
        public DateTimeOffset ended_at { get; set; }

        public override string ToString() => $"score_id: {id} user_id: {user_id}";

        public ushort? build_id { get; set; }

        [Computed]
        public Beatmap? beatmap { get; set; }

        [Computed]
        public bool is_legacy_score => legacy_score_id != null;

        public ScoreInfo ToScoreInfo()
        {
            var ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(ruleset_id);

            return new ScoreInfo
            {
                OnlineID = (long)id,
                LegacyOnlineID = (long?)legacy_score_id ?? -1,
                IsLegacyScore = is_legacy_score,
                User = new APIUser { Id = (int)user_id },
                BeatmapInfo = new BeatmapInfo
                {
                    OnlineID = (int)beatmap_id
                },
                Ruleset = new RulesetInfo { OnlineID = ruleset_id },
                Passed = passed,
                TotalScore = total_score,
                LegacyTotalScore = legacy_total_score,
                Accuracy = accuracy,
                MaxCombo = (int)max_combo,
                Rank = rank,
                Statistics = ScoreData.Statistics,
                MaximumStatistics = ScoreData.MaximumStatistics,
                Date = ended_at,
                HasOnlineReplay = has_replay,
                Mods = ScoreData.Mods.Select(m => m.ToMod(ruleset)).ToArray(),
                PP = pp,
                Ranked = ranked,
            };
        }
    }
}
