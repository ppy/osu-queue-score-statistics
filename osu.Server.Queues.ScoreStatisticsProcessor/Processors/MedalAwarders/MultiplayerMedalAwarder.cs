// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using Dapper;
using JetBrains.Annotations;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class MultiplayerMedalAwarder : IMedalAwarder
    {
        public bool RunOnFailedScores => false;
        public bool RunOnLegacyScores => false;

        public IEnumerable<Medal> Check(IEnumerable<Medal> medals, MedalAwarderContext context)
        {
            foreach (var medal in medals)
            {
                if (checkMedal(medal, context))
                    yield return medal;
            }
        }

        private static bool checkMedal(Medal medal, MedalAwarderContext context)
        {
            switch (medal.achievement_id)
            {
                // Mixtape
                case 368:
                    return checkMixtape(context);

                default:
                    return false;
            }
        }

        /// <summary>
        /// Pass every item in another user's playlist of at least 10 items.
        /// </summary>
        private static bool checkMixtape(MedalAwarderContext context)
        {
            // room this score belongs to, with its playlist size
            var room = context.Connection.QuerySingleOrDefault<(ulong roomId, uint creatorId, string? type, int itemCount)>(
                """
                SELECT `room`.`id`, `room`.`user_id`, `room`.`type`, COUNT(`room_items`.`id`)
                FROM `multiplayer_score_links` `score_link`
                JOIN `multiplayer_playlist_items` `score_item` ON `score_item`.`id` = `score_link`.`playlist_item_id`
                JOIN `multiplayer_rooms` `room` ON `room`.`id` = `score_item`.`room_id`
                JOIN `multiplayer_playlist_items` `room_items` ON `room_items`.`room_id` = `room`.`id`
                WHERE `score_link`.`score_id` = @score_id
                GROUP BY `room`.`id`, `room`.`user_id`, `room`.`type`
                """,
                new
                {
                    score_id = context.Score.id
                },
                transaction: context.Transaction);

            if (room.type != "playlists" || room.creatorId == context.Score.user_id || room.itemCount < 10)
                return false;

            // number of playlist items this user has passed in that room
            int completedItems = context.Connection.QuerySingleOrDefault<int>(
                """
                SELECT `completed`
                FROM `multiplayer_rooms_high`
                WHERE `room_id` = @room_id
                AND `user_id` = @user_id
                """,
                new
                {
                    room_id = room.roomId,
                    context.Score.user_id
                },
                transaction: context.Transaction);

            return completedItems >= room.itemCount;
        }
    }
}
