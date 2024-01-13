// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands.Queue
{
    /// <summary>
    /// Imports high scores from the osu_scores_high tables into the new solo_scores table.
    /// </summary>
    /// <remarks>
    /// This command is written under the assumption that only one importer instance is running concurrently.
    /// This is important to guarantee that scores are inserted in the same sequential order that they originally occured,
    /// which can be used for tie-breaker scenarios.
    /// </remarks>
    [Command("import-high-scores", Description = "Imports high scores from the osu_scores_high tables into the new solo_scores table.")]
    public class ImportHighScoresCommand
    {
    }
}
