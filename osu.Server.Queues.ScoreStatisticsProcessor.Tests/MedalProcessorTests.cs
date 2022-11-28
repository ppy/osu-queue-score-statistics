// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper.Contrib.Extensions;
using MySqlConnector;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class MedalProcessorTests : DatabaseTest
    {
        [Fact]
        public void TestSimplePackAwarding()
        {
            /*
                // Pass all songs in Video Game Pack vol.1

                | pack_id | beatmapset_id | min(beatmap_id) |
                |---------|---------------|-----------------|
                | 40      | 13022         | 71621           |
                | 40      | 16520         | 59225           |
                | 40      | 23073         | 79288           |
                | 40      | 27936         | 101236          |
                | 40      | 32162         | 105325          |
                | 40      | 40233         | 127762          |
                | 40      | 42158         | 132751          |
                | 40      | 42956         | 134948          |
                | 40      | 59370         | 177972          |
                | 40      | 71476         | 204837          |
                | 40      | 72137         | 206298          |
                | 40      | 102913        | 271917          |
                | 40      | 169848        | 514849          |
                | 40      | 211704        | 497769          |
             */

            WaitForDatabaseState("SELECT COUNT(*) FROM osu_user_achievements WHERE user_id = 2", 0, Cts.Token);

            pushAndInsert(71621);

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 1, Cts.Token);
            WaitForDatabaseState("SELECT COUNT(*) FROM osu_user_achievements WHERE user_id = 2", 0, Cts.Token);

            pushAndInsert(59225);
            pushAndInsert(79288);
            pushAndInsert(101236);
            pushAndInsert(105325);
            pushAndInsert(127762);
            pushAndInsert(132751);
            pushAndInsert(134948);
            pushAndInsert(177972);
            pushAndInsert(204837);
            pushAndInsert(206298);
            pushAndInsert(271917);
            pushAndInsert(514849);
            pushAndInsert(497769);

            WaitForDatabaseState("SELECT playcount FROM osu_user_stats WHERE user_id = 2", 14, Cts.Token);
            WaitForDatabaseState("SELECT COUNT(*) FROM osu_user_achievements WHERE user_id = 2", 1, Cts.Token);
        }

        private void pushAndInsert(int beatmapId)
        {
            using (MySqlConnection conn = Processor.GetDatabaseConnection())
            {
                var score = CreateTestScore(beatmapId: beatmapId);

                conn.Insert(score.Score);
                Processor.PushToQueue(score);
            }
        }
    }
}
