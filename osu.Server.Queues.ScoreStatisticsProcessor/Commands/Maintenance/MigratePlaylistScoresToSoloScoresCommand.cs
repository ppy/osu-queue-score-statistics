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
using Dapper.Contrib.Extensions;
using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("migrate-playlist-scores", Description = "Migrate scores from `multiplayer_scores` to `solo_scores`.")]
    public class MigratePlaylistScoresToSoloScoresCommand : BaseCommand
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
                if (cancellationToken.IsCancellationRequested)
                    break;

                using (var db = Queue.GetDatabaseConnection())
                {
                    var playlistItems = await db.QueryAsync<MultiplayerPlaylistItem>("SELECT * FROM multiplayer_playlist_items WHERE room_id = @PlaylistId", new
                    {
                        PlaylistId = int.Parse(id),
                    });

                    foreach (var item in playlistItems)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        MultiplayerScore[] scores = (await db.QueryAsync<MultiplayerScore>($"SELECT * FROM multiplayer_scores WHERE playlist_item_id = {item.id}")).ToArray();

                        foreach (var score in scores)
                        {
                            Console.WriteLine($"Reprocessing score {score.id} from playlist {item.id}");

                            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(item.ruleset_id);
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

                            APIMod[] roomMods = JsonConvert.DeserializeObject<APIMod[]>(item.required_mods)!;
                            APIMod[] scoreMods = JsonConvert.DeserializeObject<APIMod[]>(score.mods)!;

                            foreach (var m in roomMods)
                                Debug.Assert(scoreMods.Contains(m));

                            int? insertId = null;

                            if (score.total_score != null || score.ended_at != null)
                            {
                                var soloScoreInfo = SoloScoreInfo.ForSubmission(new ScoreInfo
                                {
                                    Statistics = statistics,
                                    MaximumStatistics = maximumStatistics,
                                    Ruleset = ruleset.RulesetInfo,
                                    MaxCombo = (int)score.max_combo!,
                                    APIMods = scoreMods,
                                    Accuracy = (double)score.accuracy!,
                                    TotalScore = (long)score.total_score!,
                                    Rank = Enum.Parse<ScoreRank>(score.rank),
                                    Passed = score.passed,
                                });

                                soloScoreInfo.StartedAt = DateTime.SpecifyKind(score.started_at, DateTimeKind.Utc);
                                soloScoreInfo.EndedAt = DateTime.SpecifyKind(score.ended_at!.Value, DateTimeKind.Utc);
                                soloScoreInfo.BeatmapID = (int)score.beatmapId;

                                insertId = await db.InsertAsync(new SoloScore
                                {
                                    user_id = (int)score.userId,
                                    beatmap_id = (int)score.beatmapId,
                                    ruleset_id = item.ruleset_id,
                                    preserve = true,
                                    ScoreInfo = soloScoreInfo,
                                    created_at = DateTime.SpecifyKind(score.created_at!.Value, DateTimeKind.Utc),
                                    updated_at = DateTime.SpecifyKind(score.updated_at!.Value, DateTimeKind.Utc),
                                });
                            }

                            await db.ExecuteAsync("INSERT INTO multiplayer_score_links (user_id, room_id, beatmap_id, playlist_item_id, score_id, created_at, updated_at) VALUES (@userId, @roomId, @beatmapId, @playlistItemId, @scoreId, @createdAt, @updatedAt)", new
                            {
                                score.userId,
                                score.roomId,
                                score.beatmapId,
                                score.playlistItemId,
                                scoreId = insertId,
                                createdAt = score.ended_at,
                                updatedAt = score.ended_at
                            });

                            Console.WriteLine();
                        }
                    }
                }
            }

            Console.WriteLine("Finished.");
            return 0;
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

        // ReSharper disable InconsistentNaming
        public class MultiplayerScore
        {
            public ulong id { get; set; }
            public uint userId { get; set; }
            public ulong roomId { get; set; }
            public ulong playlistItemId { get; set; }
            public uint beatmapId { get; set; }
            public string rank { get; set; } = string.Empty;
            public long? total_score { get; set; }
            public double? accuracy { get; set; }
            public double? pp { get; set; }
            public uint? max_combo { get; set; }
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
            public ulong id { get; set; }
            public ulong room_id { get; set; }
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
    }
}
