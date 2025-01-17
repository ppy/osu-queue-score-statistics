// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;
using Dapper.Contrib.Extensions;
using MySqlConnector;
using osu.Framework.Extensions.TypeExtensions;
using osu.Game.Rulesets;
using osu.Server.QueueProcessor;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Models.Messages;
using osu.Server.Queues.ScoreStatisticsProcessor.Processors;
using Sentry;

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
        /// version 10: modified play count and time processors to only track valid scores
        /// version 11: modified total score processor to only count valid scores
        /// </summary>
        public const int VERSION = 11;

        /// <summary>
        /// Name of environment variable which is read in order to look for external <see cref="IProcessor"/>s and <see cref="IMedalAwarder"/>s.
        /// The expected value is a colon-delimited list of direct paths to DLLs or paths to directories (all .dll files within will be loaded).
        /// </summary>
        private const string external_processor_path_envvar = "EXTERNAL_PROCESSOR_PATH";

        public static readonly List<Ruleset> AVAILABLE_RULESETS = getRulesets();

        private readonly List<IProcessor> processors;

        private readonly ElasticQueuePusher elasticQueueProcessor = new ElasticQueuePusher();

        /// <summary>
        /// Creates a new <see cref="ScoreStatisticsQueueProcessor"/>
        /// </summary>
        /// <param name="disabledProcessors">List of names of processors to disable.</param>
        /// <param name="externalProcessorAssemblies">
        /// List of names of assemblies to load. Mostly for use by external <see cref="IProcessor"/>s and <see cref="IMedalAwarder"/>s.
        /// Can also be specified at runtime via  <see cref="external_processor_path_envvar"/>.
        /// </param>
        public ScoreStatisticsQueueProcessor(string[]? disabledProcessors = null, AssemblyName[]? externalProcessorAssemblies = null)
            : base(new QueueConfiguration { InputQueueName = Environment.GetEnvironmentVariable("SCORES_PROCESSING_QUEUE") ?? "score-statistics" })
        {
            DapperExtensions.InstallDateTimeOffsetMapper();

            loadExternalProcessorAssemblies(externalProcessorAssemblies);
            processors = createProcessors(disabledProcessors);
        }

        private void loadExternalProcessorAssemblies(AssemblyName[]? externalProcessorAssemblies)
        {
            string[]? pathsFromEnvironment = Environment.GetEnvironmentVariable(external_processor_path_envvar)?.Split(':', StringSplitOptions.RemoveEmptyEntries);

            if (pathsFromEnvironment == null || pathsFromEnvironment.Length == 0)
                return;

            foreach (string path in pathsFromEnvironment)
            {
                if (Directory.Exists(path))
                {
                    foreach (string dll in Directory.GetFiles(path, "*.dll"))
                        loadAssembly(dll);
                    continue;
                }

                if (File.Exists(path))
                {
                    loadAssembly(path);
                    continue;
                }

                throw new ArgumentException($"File {path} specified in {external_processor_path_envvar} not found!");
            }

            if (externalProcessorAssemblies != null)
            {
                foreach (var assemblyName in externalProcessorAssemblies)
                    Assembly.Load(assemblyName);
            }

            void loadAssembly(string path)
            {
                Console.WriteLine($"Loading assembly from {path}...");
                var loaded = Assembly.LoadFile(path);
                Console.WriteLine($"Loaded {loaded.FullName}.");
            }
        }

        private List<IProcessor> createProcessors(string[]? disabledProcessors)
        {
            List<Type> enabledTypes = AppDomain.CurrentDomain
                                               .GetAssemblies()
                                               .SelectMany(a => a.GetTypes())
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

            var instances = new List<IProcessor>();

            foreach (var t in enabledTypes)
            {
                if (Activator.CreateInstance(t) is IProcessor processor)
                    instances.Add(processor);
            }

            Console.WriteLine("Active processors:");
            foreach (var instance in instances)
                Console.WriteLine(instance.DisplayString);

            Console.WriteLine();

            Console.WriteLine("Disabled processors:");
            foreach (var type in disabledTypes)
                Console.WriteLine($"- {type.ReadableName()} ({GetType().Assembly.FullName})");

            Console.WriteLine();

            return instances.OrderBy(processor => processor.Order).ToList();
        }

        /// <summary>
        /// Process the provided score item.
        /// </summary>
        /// <param name="item">The score to process.</param>
        /// <param name="force">Whether to process regardless of whether the attached process history implies it is already processed up-to-date.</param>
        public void ProcessScore(ScoreItem item, bool force) => processScore(item, force);

        protected override void ProcessResult(ScoreItem item) => processScore(item, false);

        private void processScore(ScoreItem item, bool force = false)
        {
            var stopwatch = new Stopwatch();
            var tags = new List<string>();

            try
            {
                SentrySdk.ConfigureScope(scope =>
                {
                    scope.SetTag("score_id", item.Score.id.ToString());
                    scope.User = new SentryUser
                    {
                        Id = item.Score.user_id.ToString(),
                    };
                });

                tags.Add($"ruleset:{item.Score.ruleset_id}");

                if (item.Score.legacy_score_id != null)
                    tags.Add("type:legacy");

                if (item.ProcessHistory?.processed_version == VERSION && !force)
                {
                    tags.Add("type:skipped");
                    return;
                }

                using (var conn = GetDatabaseConnection())
                {
                    var score = item.Score;

                    score.beatmap = conn.QuerySingleOrDefault<Beatmap>("SELECT * FROM osu_beatmaps WHERE `beatmap_id` = @BeatmapId", new
                    {
                        BeatmapId = score.beatmap_id
                    });
                    score.beatmap!.beatmapset = conn.QuerySingleOrDefault<BeatmapSet>("SELECT * FROM `osu_beatmapsets` WHERE `beatmapset_id` = @BeatmapSetId", new
                    {
                        BeatmapSetId = score.beatmap.beatmapset_id
                    });

                    using (var transaction = conn.BeginTransaction(IsolationLevel.ReadCommitted))
                    {
                        var userStats = DatabaseHelper.GetUserStatsAsync(score, conn, transaction).Result;

                        if (userStats == null)
                        {
                            // ruleset could be invalid
                            // TODO: add check in client and server to not submit unsupported rulesets
                            tags.Add("type:no-stats");
                            return;
                        }

                        // if required, we can rollback any previous version of processing then reapply with the latest.
                        if (item.ProcessHistory != null)
                        {
                            tags.Add("type:upgraded");
                            byte version = item.ProcessHistory.processed_version;

                            foreach (var p in enumerateValidProcessors(score))
                                p.RevertFromUserStats(score, userStats, version, conn, transaction);
                        }
                        else
                        {
                            tags.Add("type:new");
                        }

                        item.Tags = tags.ToArray();

                        foreach (IProcessor p in enumerateValidProcessors(score))
                        {
                            stopwatch.Restart();
                            p.ApplyToUserStats(score, userStats, conn, transaction);
                            DogStatsd.Timer($"apply-{p.GetType().ReadableName()}", stopwatch.ElapsedMilliseconds, tags: item.Tags);
                        }

                        DatabaseHelper.UpdateUserStatsAsync(userStats, conn, transaction).Wait();

                        updateHistoryEntry(item, conn, transaction);

                        transaction.Commit();
                    }

                    foreach (var p in enumerateValidProcessors(score))
                        p.ApplyGlobal(score, conn);

                    if (score.passed && !score.preserve)
                        Console.WriteLine($"Passed score {score.id} was processed but not preserved");
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
            finally
            {
                item.Tags = tags.ToArray();
            }
        }

        private IEnumerable<IProcessor> enumerateValidProcessors(SoloScore score)
        {
            IEnumerable<IProcessor> result = processors;

            if (!score.passed)
                result = result.Where(p => p.RunOnFailedScores);

            if (score.is_legacy_score)
                result = result.Where(p => p.RunOnLegacyScores);

            return result;
        }

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
