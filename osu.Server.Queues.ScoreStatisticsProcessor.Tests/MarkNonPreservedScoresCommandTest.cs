// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using Dapper;
using osu.Server.Queues.ScoreStatisticsProcessor.Commands.Maintenance;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class MarkNonPreservedScoresCommandTest : DatabaseTest
    {
        private readonly Beatmap beatmap;

        public MarkNonPreservedScoresCommandTest()
        {
            beatmap = AddBeatmap();

            using var db = Processor.GetDatabaseConnection();

            db.Execute("DELETE FROM `multiplayer_playlist_item_scores`");
            db.Execute("TRUNCATE TABLE `score_pins`");
        }

        [Fact]
        public async Task OnlyBestPPAndTotalScoresArePreserved()
        {
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.id = 1;
                s.Score.total_score = 800_000;
                s.Score.pp = 95;
            });
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.id = 2;
                s.Score.total_score = 850_000;
                s.Score.pp = 90;
            });
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.id = 3;
                s.Score.total_score = 500_000;
                s.Score.pp = 85;
            });
            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 1", 3, CancellationToken);
            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 0", 0, CancellationToken);

            var command = new MarkNonPreservedScoresCommand { RulesetId = 0 };
            await command.OnExecuteAsync(CancellationToken);
            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 1", 2, CancellationToken);
            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 0", 1, CancellationToken);
            WaitForDatabaseState("SELECT `preserve` FROM `scores` WHERE `id` = 3", false, CancellationToken);
        }

        [Fact]
        public async Task NonBestScoresRemainPreservedIfPinned()
        {
            using var db = Processor.GetDatabaseConnection();

            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.id = 1;
                s.Score.total_score = 800_000;
                s.Score.pp = 95;
            });
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.id = 2;
                s.Score.total_score = 500_000;
                s.Score.pp = 85;
            });
            await db.ExecuteAsync("INSERT INTO `score_pins` (`user_id`, `score_type`, `score_id`, `ruleset_id`, `display_order`) VALUES (2, 'solo_score', 2, 0, 0)");

            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 1", 2, CancellationToken);
            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 0", 0, CancellationToken);

            var command = new MarkNonPreservedScoresCommand { RulesetId = 0 };
            await command.OnExecuteAsync(CancellationToken);
            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 1", 2, CancellationToken);
            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 0", 0, CancellationToken);
            WaitForDatabaseState("SELECT `preserve` FROM `scores` WHERE `id` = 2", true, CancellationToken);
        }

        [Fact]
        public async Task NonBestScoresRemainPreservedIfAssociatedWithPlaylistItem()
        {
            using var db = Processor.GetDatabaseConnection();

            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.id = 1;
                s.Score.total_score = 800_000;
                s.Score.pp = 95;
            });
            SetScoreForBeatmap(beatmap.beatmap_id, s =>
            {
                s.Score.id = 2;
                s.Score.total_score = 500_000;
                s.Score.pp = 85;
            });
            await db.ExecuteAsync("INSERT INTO `multiplayer_playlist_item_scores` (`user_id`, `playlist_item_id`, `score_id`) VALUES (2, 1, 2)");

            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 1", 2, CancellationToken);
            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 0", 0, CancellationToken);

            var command = new MarkNonPreservedScoresCommand { RulesetId = 0 };
            await command.OnExecuteAsync(CancellationToken);
            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 1", 2, CancellationToken);
            WaitForDatabaseState("SELECT COUNT(1) FROM `scores` WHERE `preserve` = 0", 0, CancellationToken);
            WaitForDatabaseState("SELECT `preserve` FROM `scores` WHERE `id` = 2", true, CancellationToken);
        }
    }
}
