// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics.CodeAnalysis;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Models
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [Serializable]
    public class Medal
    {
        public int achievement_id;
        public string name = string.Empty;
        public string description = string.Empty;
        public string slug = string.Empty;
        public string image = string.Empty;
        public string grouping = string.Empty;
        public int progression;
        public int ordering;
        public bool enabled;
        public int? mode;

        // probably not of immediate interest.
        public int? quest_ordering;
        public string quest_instructions = string.Empty;
    }
}
