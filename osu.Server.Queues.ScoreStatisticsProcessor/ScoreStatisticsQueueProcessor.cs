// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Framework.Extensions.TypeExtensions;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Models.Messages;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public class ScoreStatisticsQueueProcessor : QueueProcessor<ScoreItem>
    {
        /// <summary>
        /// version 1: basic playcount
        /// version 2: total score, hit statistics, beatmap playcount, monthly playcount, max combo
        /// version 3: fixed incorrect revert condition for beatmap/monthly playcount
        /// version 4: uses SoloScore"V2" (moving all content to json data block)
        /// version 5: added performance processor
        /// version 6: added play time processor
        /// version 7: added user rank count processor
        /// version 8: switched total score processor from standardised score to classic score
        /// version 9: added ranked score processor
        /// </summary>
        public const int VERSION = 9;

        public static readonly List<Ruleset> AVAILABLE_RULESETS = getRulesets();

        private readonly List<IProcessor> processors = new List<IProcessor>();

        private readonly ElasticQueuePusher elasticQueueProcessor = new ElasticQueuePusher();

        public ScoreStatisticsQueueProcessor(string[]? disabledProcessors = null)
            : base(new QueueConfiguration { InputQueueName = "score-statistics" })
        {
            DapperExtensions.InstallDateTimeOffsetMapper();

            List<Type> enabledTypes = createProcessors(disabledProcessors);

            foreach (var t in enabledTypes)
            {
                if (Activator.CreateInstance(t) is IProcessor processor)
                    processors.Add(processor);
            }

            processors = processors.OrderBy(p => p.Order).ToList();
        }

        private List<Type> createProcessors(string[]? disabledProcessors)
        {
            List<Type> enabledTypes = typeof(ScoreStatisticsQueueProcessor)
                                      .Assembly
                                      .GetTypes()
                                      .Where(t => !t.IsInterface && typeof(IProcessor).IsAssignableFrom(t))
                                      .ToList();

            List<Type> disabledTypes = new List<Type>();

            if (disabledProcessors?.Length > 0)
            {
                foreach (string s in disabledProcessors)
                {
                    var match = enabledTypes.FirstOrDefault(t => t.ReadableName() == s);

                    if (match == null)
                        throw new ArgumentException($"Could not find matching processor to disable (\"{s}\")");

                    enabledTypes.Remove(match);
                    disabledTypes.Add(match);
                }
            }

            Console.WriteLine("Active processors:");
            foreach (var type in enabledTypes)
                Console.WriteLine($"- {type.ReadableName()}");

            Console.WriteLine();

            Console.WriteLine("Disabled processors:");
            foreach (var type in disabledTypes)
                Console.WriteLine($"- {type.ReadableName()}");

            Console.WriteLine();

            return enabledTypes;
        }

        protected override void ProcessResult(ScoreItem item)
        {
            if (item.Score.ScoreInfo.LegacyScoreId != null)
            {
                item.Tags = new[] { "type:legacy" };
                return;
            }

            if (item.ProcessHistory?.processed_version == VERSION)
            {
                item.Tags = new[] { "type:skipped" };
                return;
            }

            try
            {
                using (var conn = GetDatabaseConnection())
                {
                    var scoreRow = item.Score;
                    var score = scoreRow.ScoreInfo;

                    score.Beatmap = conn.QuerySingleOrDefault<Beatmap>("SELECT * FROM osu_beatmaps WHERE `beatmap_id` = @BeatmapId", new
                    {
                        BeatmapId = score.BeatmapID
                    })?.ToAPIBeatmap();

                    using (var transaction = conn.BeginTransaction())
                    {
                        var userStats = DatabaseHelper.GetUserStatsAsync(score, conn, transaction).Result;

                        if (userStats == null)
                        {
                            // ruleset could be invalid
                            // TODO: add check in client and server to not submit unsupported rulesets
                            item.Tags = new[] { "type:no-stats" };
                            return;
                        }

                        // if required, we can rollback any previous version of processing then reapply with the latest.
                        if (item.ProcessHistory != null)
                        {
                            item.Tags = new[] { "type:upgraded" };
                            byte version = item.ProcessHistory.processed_version;

                            foreach (var p in enumerateValidProcessors(score))
                                p.RevertFromUserStats(score, userStats, version, conn, transaction);
                        }
                        else
                        {
                            item.Tags = new[] { "type:new" };
                        }

                        foreach (var p in enumerateValidProcessors(score))
                            p.ApplyToUserStats(score, userStats, conn, transaction);

                        DatabaseHelper.UpdateUserStatsAsync(userStats, conn, transaction).Wait();

                        updateHistoryEntry(item, conn, transaction);

                        if (score.Passed)
                        {
                            // For now, just assume all passing scores are to be preserved.
                            conn.Execute("UPDATE solo_scores SET preserve = 1 WHERE id = @Id", new { Id = score.ID }, transaction);
                        }

                        transaction.Commit();
                    }

                    foreach (var p in enumerateValidProcessors(score))
                        p.ApplyGlobal(score, conn);
                }

                elasticQueueProcessor.PushToQueue(new ElasticQueuePusher.ElasticScoreItem
                {
                    ScoreId = (long)item.Score.id,
                });
                publishScoreProcessed(item);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
                throw;
            }
        }

        private IEnumerable<IProcessor> enumerateValidProcessors(SoloScoreInfo score) => score.Passed ? processors : processors.Where(p => p.RunOnFailedScores);

        private static void updateHistoryEntry(ScoreItem item, MySqlConnection db, MySqlTransaction transaction)
        {
            bool hadHistory = item.ProcessHistory != null;

            item.MarkProcessed();

            if (hadHistory)
                db.Update(item.ProcessHistory, transaction);
            else
                db.Insert(item.ProcessHistory, transaction);
        }

        private void publishScoreProcessed(ScoreItem item)
        {
            Debug.Assert(item.ProcessHistory != null);

            try
            {
                PublishMessage("osu-channel:score:processed", new ScoreProcessed
                {
                    ScoreId = item.ProcessHistory.score_id,
                    ProcessedVersion = item.ProcessHistory.processed_version
                });
            }
            catch (Exception ex)
            {
                // failure to deliver this message is not critical, so catch locally.
                Console.WriteLine($"Error publishing {nameof(ScoreProcessed)} event: {ex}");
            }
        }

        private static List<Ruleset> getRulesets()
        {
            const string ruleset_library_prefix = "osu.Game.Rulesets";

            var rulesetsToProcess = new List<Ruleset>();

            foreach (string file in Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, $"{ruleset_library_prefix}.*.dll"))
            {
                try
                {
                    var assembly = Assembly.LoadFrom(file);
                    Type type = assembly.GetTypes().First(t => t.IsPublic && t.IsSubclassOf(typeof(Ruleset)));
                    rulesetsToProcess.Add((Ruleset)Activator.CreateInstance(type)!);
                }
                catch
                {
                    throw new Exception($"Failed to load ruleset ({file})");
                }
            }

            return rulesetsToProcess;
        }
    }
}
