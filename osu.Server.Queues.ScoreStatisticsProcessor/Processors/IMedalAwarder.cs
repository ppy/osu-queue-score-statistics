// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using JetBrains.Annotations;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors
{
    /// <summary>
    /// A class which handles conditional awarding of one of more medals.
    /// </summary>
    [UsedImplicitly]
    public interface IMedalAwarder
    {
        /// <summary>
        /// Whether this awarder should be run on failed scores.
        /// </summary>
        bool RunOnFailedScores { get; }

        /// <summary>
        /// For a given score and collection of valid medals, check which should be awarded (if any).
        /// </summary>
        /// <param name="medals">All medals available for potential awarding.</param>
        /// <param name="context">Contains all necessary context and access method for implementing <see cref="IMedalAwarder"/>s.</param>
        /// <returns></returns>
        IEnumerable<Medal> Check(IEnumerable<Medal> medals, MedalAwarderContext context);
    }
}
