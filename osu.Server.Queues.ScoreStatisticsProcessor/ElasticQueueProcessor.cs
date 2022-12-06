// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Server.QueueProcessor;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    /// <summary>
    /// Minimal implementation of queue processor to push to queue.
    /// This should be replaced with a dependency on `osu-elastic-indexer` ASAP.
    /// </summary>
    public class ElasticQueueProcessor : QueueProcessor<ElasticQueueProcessor.ElasticScoreItem>
    {
        private static readonly string queue_name = $"score-index-{Environment.GetEnvironmentVariable("SCHEMA")}";

        public ElasticQueueProcessor()
            : base(new QueueConfiguration { InputQueueName = queue_name })
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCHEMA")))
                throw new ArgumentException("Elasticsearch schema should be specified via environment variable SCHEMA");

            // TODO: automate schema version lookup
            // see https://github.com/ppy/osu-elastic-indexer/blob/316e3e2134933e22363f4911e0be4175984ae15e/osu.ElasticIndexer/Redis.cs#L10
        }

        protected override void ProcessResult(ElasticScoreItem scoreItem)
        {
            throw new NotImplementedException();
        }

        [Serializable]
        public class ElasticScoreItem : QueueItem
        {
            public long? ScoreId { get; init; }
        }
    }
}
