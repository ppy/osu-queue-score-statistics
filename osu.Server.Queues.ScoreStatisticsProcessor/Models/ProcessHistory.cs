// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Table("solo_scores_process_history")]
    public class ProcessHistory
    {
        [ExplicitKey]
        public long id { get; set; }

        public byte processed_version { get; set; }

        // For now we are completely ignoring this field in the interest of keeping things simple.
        // There are two issues with making this work:
        // 1. our sql servers still default to SYSTEM time, which needs to be fixed server-side or in the GetDatabaseConnection method in OsuQueueProcessor.
        // 2. c# stores more precision than mysql for times, which means serialisation tests will fail with sub-second precision errors.
        // public DateTimeOffset processed_at { get; set; } = DateTimeOffset.UtcNow;
    }
}
