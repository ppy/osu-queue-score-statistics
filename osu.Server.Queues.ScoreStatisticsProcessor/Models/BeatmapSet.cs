// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;
using Dapper.Contrib.Extensions;
using osu.Game.Beatmaps;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    [Table(TABLE_NAME)]
    public class BeatmapSet
    {
        public const string TABLE_NAME = "osu_beatmapsets";

        [ExplicitKey]
        public int beatmapset_id { get; set; }

        public BeatmapOnlineStatus approved { get; set; }
    }
}
