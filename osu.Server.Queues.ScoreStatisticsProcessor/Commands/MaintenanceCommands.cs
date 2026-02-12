// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Commands
{
    [Command(Name = "maintenance", Description = "General database maintenance commands which are usually run on a cron schedule.")]
    [Subcommand(typeof(DeleteNonPreservedScoresCommand))]
    [Subcommand(typeof(MarkNonPreservedScoresCommand))]
    [Subcommand(typeof(MigratePlaylistScoresToSoloScoresCommand))]
    [Subcommand(typeof(MigrateSoloScoresCommand))]
    [Subcommand(typeof(VerifyImportedScoresCommand))]
    [Subcommand(typeof(VerifyScoreRanksCommand))]
    [Subcommand(typeof(ReorderIncorrectlyImportedTiedScoresCommand))]
    [Subcommand(typeof(ReindexBeatmapCommand))]
    [Subcommand(typeof(DeleteImportedHighScoresCommand))]
    [Subcommand(typeof(VerifyReplaysExistCommand))]
    [Subcommand(typeof(VerifyUserRankCounts))]
    [Subcommand(typeof(VerifyUserRankedScore))]
    [Subcommand(typeof(PopulateTotalScoreWithoutModsCommand))]
    [Subcommand(typeof(RecalculateModMultipliersCommand))]
    public sealed class MaintenanceCommands
    {
        public Task<int> OnExecuteAsync(CommandLineApplication app, CancellationToken cancellationToken)
        {
            app.ShowHelp(false);
            return Task.FromResult(1);
        }
    }
}
