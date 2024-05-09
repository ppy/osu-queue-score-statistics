// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public class ElasticQueuePusher
    {
        private readonly List<ElasticQueueProcessor> processors = new List<ElasticQueueProcessor>();

        public ElasticQueuePusher()
        {
            using (var redis = RedisAccess.GetConnection())
            {
                string?[] schemas = redis.GetActiveSchemas();

                foreach (string? schema in schemas)
                {
                    // Old schemas that don't have the prefix in the active list.
                    // We don't want to push to them.
                    if (schema?.Length > 2)
                        processors.Add(new ElasticQueueProcessor(schema));
                }
            }
        }

        public string ActiveQueues => string.Join(',', processors.Select(p => p.QueueName));

        public void PushToQueue(ElasticScoreItem elasticScoreItem)
        {
            foreach (var p in processors)
                p.PushToQueue(elasticScoreItem);
        }

        public void PushToQueue(List<ElasticScoreItem> items)
        {
            foreach (var p in processors)
                p.PushToQueue(items);
        }

        [Serializable]
        public class ElasticScoreItem : QueueItem
        {
            public long? ScoreId { get; init; }
        }

        /// <summary>
        /// Minimal implementation of queue processor to push to queue.
        /// This should be replaced with a dependency on `osu-elastic-indexer` ASAP.
        /// </summary>
        private class ElasticQueueProcessor : QueueProcessor<ElasticScoreItem>
        {
            public ElasticQueueProcessor(string schema)
                : base(new QueueConfiguration { InputQueueName = schema })
            {
            }

            protected override void ProcessResult(ElasticScoreItem scoreItem)
            {
                throw new NotImplementedException();
            }
        }
    }
}
