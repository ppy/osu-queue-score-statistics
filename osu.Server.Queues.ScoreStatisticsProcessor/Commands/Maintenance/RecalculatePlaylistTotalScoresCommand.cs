// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;

// ReSharper disable InconsistentNaming
namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("recalculate-playlist-scores", Description = "Process all scores in a specific playlist, recalculating and writing any changes.")]
    public class RecalculatePlaylistTotalScoresCommand : BaseCommand
    {
        /// <summary>
        /// The playlist room ID to reprocess.
        /// </summary>
        [Required]
        [Argument(0, Description = "Command separated list of playlist room IDs to reprocess.")]
        public string PlaylistIds { get; set; } = string.Empty;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            foreach (string id in PlaylistIds.Split(','))
            {
                using (var db = Queue.GetDatabaseConnection())
                {
                    var playlistItems = await db.QueryAsync<MultiplayerPlaylistItem>("SELECT * FROM multiplayer_playlist_items WHERE room_id = @PlaylistId", new
                    {
                        PlaylistId = int.Parse(id),
                    });

                    foreach (var item in playlistItems)
                    {
                        MultiplayerScore[] scores = (await db.QueryAsync<MultiplayerScore>($"SELECT * FROM multiplayer_scores WHERE playlist_item_id = {item.id}")).ToArray();

                        foreach (var score in scores)
                        {
                            long? scoreBefore = score.total_score;

                            if (scoreBefore == null || !score.passed)
                                continue;

                            Console.WriteLine($"Reprocessing score {score.id} from playlist {item.id}");

                            long scoreAfter = ensureCorrectTotalScore(score, item);

                            if (scoreAfter != scoreBefore)
                            {
                                Console.WriteLine($"Score requires update ({scoreBefore} -> {scoreAfter})");

                                await db.ExecuteAsync($"UPDATE multiplayer_scores SET total_score = {scoreAfter} WHERE id = {score.id}");
                            }
                            else
                                Console.WriteLine("Score is correct");

                            Console.WriteLine();
                        }
                    }
                }
            }

            Console.WriteLine("Finished.");
            return 0;
        }

        private long ensureCorrectTotalScore(MultiplayerScore score, MultiplayerPlaylistItem playlistItem)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(playlistItem.ruleset_id);

            HitResult maxRulesetJudgement = ruleset.GetHitResults().First().result;

            Dictionary<HitResult, int> statistics = JsonConvert.DeserializeObject<Dictionary<HitResult, int>>(score.statistics)!;

            List<HitResult> allHits = statistics
                                      .SelectMany(kvp => Enumerable.Repeat(kvp.Key, kvp.Value))
                                      .ToList();

            var maximumStatistics = new Dictionary<HitResult, int>();

            foreach (var groupedStats in allHits
                                         .Select(r => getMaxJudgementFor(r, maxRulesetJudgement))
                                         .GroupBy(r => r))
            {
                maximumStatistics[groupedStats.Key] = groupedStats.Count();
            }

            APIMod[] roomMods = JsonConvert.DeserializeObject<APIMod[]>(playlistItem.required_mods)!;
            APIMod[] scoreMods = JsonConvert.DeserializeObject<APIMod[]>(score.mods)!;

            foreach (var m in roomMods)
                Debug.Assert(scoreMods.Contains(m));

            Console.WriteLine($"Mods: {string.Join(',', scoreMods.Select(m => m.ToString()))}");

            ScoreInfo scoreInfo = new ScoreInfo
            {
                Statistics = statistics,
                MaximumStatistics = maximumStatistics,
                Ruleset = ruleset.RulesetInfo,
                MaxCombo = score.max_combo,
                APIMods = scoreMods
            };

            return StandardisedScoreMigrationTools.GetOldStandardised(scoreInfo);
        }

        public class MultiplayerScore
        {
            public long id { get; set; }
            public uint userId { get; set; }
            public long roomId { get; set; }
            public long playlistItemId { get; set; }
            public long beatmapId { get; set; }
            public string rank { get; set; } = string.Empty;
            public long? total_score { get; set; }
            public double? accuracy { get; set; }
            public double? pp { get; set; }
            public int max_combo { get; set; }
            public string mods { get; set; } = string.Empty;
            public string statistics { get; set; } = string.Empty;
            public DateTime started_at { get; set; }
            public DateTime? ended_at { get; set; }
            public bool passed { get; set; }
            public DateTime? created_at { get; set; }
            public DateTime? updated_at { get; set; }
            public DateTime? deleted_at { get; set; }
        }

        public class MultiplayerPlaylistItem
        {
            public long id { get; set; }
            public long room_id { get; set; }
            public uint owner_id { get; set; }
            public uint beatmap_id { get; set; }
            public ushort ruleset_id { get; set; }
            public ushort? playlist_order { get; set; }
            public string allowed_mods { get; set; } = string.Empty;
            public string required_mods { get; set; } = string.Empty;
            public byte? max_attempts { get; set; }
            public DateTime? created_at { get; set; }
            public DateTime? updated_at { get; set; }
            public bool expired { get; set; }
            public DateTime? played_at { get; set; }
        }

        private static HitResult getMaxJudgementFor(HitResult hitResult, HitResult max)
        {
            switch (hitResult)
            {
                case HitResult.Miss:
                case HitResult.Meh:
                case HitResult.Ok:
                case HitResult.Good:
                case HitResult.Great:
                case HitResult.Perfect:
                    return max;

                case HitResult.SmallTickMiss:
                case HitResult.SmallTickHit:
                    return HitResult.SmallTickHit;

                case HitResult.LargeTickMiss:
                case HitResult.LargeTickHit:
                    return HitResult.LargeTickHit;
            }

            return HitResult.IgnoreHit;
        }
    }
}
