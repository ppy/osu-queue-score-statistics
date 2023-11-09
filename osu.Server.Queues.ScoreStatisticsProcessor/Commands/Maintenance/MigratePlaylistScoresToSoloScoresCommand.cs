// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using McMaster.Extensions.CommandLineUtils;
using MySqlConnector;
using Newtonsoft.Json;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance
{
    [Command("migrate-playlist-scores", Description = "Migrate scores from `multiplayer_scores` to `solo_scores`.")]
    public class MigratePlaylistScoresToSoloScoresCommand : BaseCommand
    {
        /// <summary>
        /// The playlist room ID to reprocess.
        /// </summary>
        [Option("--playlist-ids", Description = "Command separated list of playlist room IDs to reprocess.")]
        public string PlaylistIds { get; set; } = string.Empty;

        public async Task<int> OnExecuteAsync(CancellationToken cancellationToken)
        {
            using var db = Queue.GetDatabaseConnection();

            int[] playlistIds;

            if (!string.IsNullOrEmpty(PlaylistIds))
            {
                playlistIds = PlaylistIds.Split(',').Select(int.Parse).ToArray();
            }
            else
            {
                Console.WriteLine("Finding non-migrated playlists");
                playlistIds = (await db.QueryAsync<int>("SELECT id FROM multiplayer_rooms WHERE type = 'playlists' AND id NOT IN (SELECT DISTINCT(room_id) FROM multiplayer_score_links)"))
                    .ToArray();
            }

            Console.WriteLine($"Running on {playlistIds.Length} playlists");
            Thread.Sleep(5000);

            using var insertCommand = db.CreateCommand();
            insertCommand.CommandText = "INSERT INTO solo_scores (user_id, beatmap_id, ruleset_id, data, preserve, created_at, updated_at) VALUES (@user_id, @beatmap_id, @ruleset_id, @data, @preserve, @created_at, @updated_at)";

            var paramUserId = insertCommand.Parameters.Add("user_id", DbType.UInt32);
            var paramBeatmapId = insertCommand.Parameters.Add("beatmap_id", MySqlDbType.UInt24);
            var paramRulesetId = insertCommand.Parameters.Add("ruleset_id", DbType.UInt16);
            var paramData = insertCommand.Parameters.Add("data", MySqlDbType.JSON);
            var paramPreserve = insertCommand.Parameters.Add("preserve", DbType.Boolean);
            var paramCreatedAt = insertCommand.Parameters.Add("created_at", MySqlDbType.Timestamp);
            var paramUpdatedAt = insertCommand.Parameters.Add("updated_at", MySqlDbType.Timestamp);

            await insertCommand.PrepareAsync(cancellationToken);

            foreach (int id in playlistIds)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                Console.WriteLine($"Processing playlist {id}...");

                var playlistItems = await db.QueryAsync<MultiplayerPlaylistItem>("SELECT * FROM multiplayer_playlist_items WHERE room_id = @PlaylistId", new
                {
                    PlaylistId = id,
                });

                foreach (var item in playlistItems)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    MultiplayerScore[] scores = (await db.QueryAsync<MultiplayerScore>($"SELECT * FROM multiplayer_scores WHERE playlist_item_id = {item.id}")).ToArray();

                    Console.Write($"Processing {scores.Length} scores for playlist item {item.id}");

                    foreach (var score in scores)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        Console.Write(".");
                        Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(item.ruleset_id);
                        HitResult maxRulesetJudgement = ruleset.GetHitResults().First().result;

                        Dictionary<HitResult, int> statistics = JsonConvert.DeserializeObject<Dictionary<HitResult, int>>(score.statistics)
                                                                ?? new Dictionary<HitResult, int>();

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

                        long? insertId = null;

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
                            soloScoreInfo.BeatmapID = (int)score.beatmap_id;

                            paramUserId.Value = score.user_id;
                            paramBeatmapId.Value = score.beatmap_id;
                            paramRulesetId.Value = item.ruleset_id;
                            paramPreserve.Value = true;
                            paramData.Value = JsonConvert.SerializeObject(soloScoreInfo);
                            paramCreatedAt.Value = DateTime.SpecifyKind(score.created_at!.Value, DateTimeKind.Utc);
                            paramUpdatedAt.Value = DateTime.SpecifyKind(score.updated_at!.Value, DateTimeKind.Utc);

                            await insertCommand.ExecuteNonQueryAsync(cancellationToken);

                            insertId = insertCommand.LastInsertedId;
                        }

                        long scoreLinkId = await db.QuerySingleAsync<long>("INSERT INTO multiplayer_score_links (user_id, room_id, beatmap_id, playlist_item_id, score_id, created_at, updated_at) VALUES (@userId, @roomId, @beatmapId, @playlistItemId, @scoreId, @createdAt, @updatedAt); SELECT LAST_INSERT_ID()", new
                        {
                            userId = score.user_id,
                            roomId = score.room_id,
                            beatmapId = score.beatmap_id,
                            playlistItemId = score.playlist_item_id,
                            scoreId = insertId,
                            createdAt = score.created_at,
                            updatedAt = score.ended_at
                        });

                        await db.ExecuteAsync("UPDATE multiplayer_scores_high SET score_link_id = @scoreLinkId, score_id = 0 WHERE user_id = @userId AND playlist_item_id = @playlistItemId AND score_id = @scoreId", new
                        {
                            scoreLinkId = scoreLinkId,
                            userId = score.user_id,
                            playlistItemId = score.playlist_item_id,
                            scoreId = score.id,
                        });
                    }

                    Console.WriteLine();
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
            public uint user_id { get; set; }
            public ulong room_id { get; set; }
            public ulong playlist_item_id { get; set; }
            public uint beatmap_id { get; set; }
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
