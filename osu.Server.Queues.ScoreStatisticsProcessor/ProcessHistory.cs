// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Table("solo_scores_process_history")]
    public class ProcessHistory
    {
        public ProcessHistory(long id, byte processedVersion)
        {
            this.id = id;
            processed_version = processedVersion;
        }

        [ExplicitKey]
        public long id { get; set; }

        public DateTimeOffset processed_at { get; set; } = DateTimeOffset.Now;

        public byte processed_version { get; set; }
    }
}
