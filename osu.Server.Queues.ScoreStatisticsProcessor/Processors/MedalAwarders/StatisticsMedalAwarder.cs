using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using System.Collections.Generic;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class StatisticsMedalAwarder : IMedalAwarder
    {
        public bool RunOnFailedScores => false;

        public IEnumerable<Medal> Check(SoloScoreInfo score, UserStats userStats, IEnumerable<Medal> medals, MySqlConnection conn, MySqlTransaction transaction)
        {
            foreach (var medal in medals)
            {
                if (checkMedal(score, medal, userStats))
                    yield return medal;
            }
        }

        private bool checkMedal(SoloScoreInfo score, Medal medal, UserStats stats)
        {
            return medal.achievement_id switch
            {
                // osu!standard
                // 500 Combo
                1 => score.MaxCombo >= 500,
                // 750 Combo
                3 => score.MaxCombo >= 750,
                // 1000 Combo
                4 => score.MaxCombo >= 1000,
                // 2000 Combo
                5 => score.MaxCombo >= 2000,
                // 5000 Plays
                20 => stats.playcount >= 5000,
                // 15000 Plays
                21 => stats.playcount >= 15000,
                // 25000 Plays
                22 => stats.playcount >= 25000,
                // 50000 Plays
                28 => stats.playcount >= 50000,

                // osu!taiko
                // 30000 Drum Hits
                31 => (stats.count50 + stats.count100 + stats.count300) >= 30000,
                // 300000 Drum Hits
                32 => (stats.count50 + stats.count100 + stats.count300) >= 300000,
                // 3000000 Drum Hits
                33 => (stats.count50 + stats.count100 + stats.count300) >= 3000000,
                // 30000000 Drum Hits
                291 => (stats.count50 + stats.count100 + stats.count300) >= 30000000,

                // osu!catch
                // Catch 20000 Fruits
                13 => (stats.count50 + stats.count100 + stats.count300) >= 20000,
                // Catch 200000 Fruits
                23 => (stats.count50 + stats.count100 + stats.count300) >= 200000,
                // Catch 2000000 Fruits
                24 => (stats.count50 + stats.count100 + stats.count300) >= 2000000,
                // Catch 20000000 Fruits
                292 => (stats.count50 + stats.count100 + stats.count300) >= 20000000,

                // osu!mania
                // 40000 Keys
                46 => (stats.count50 + stats.count100 + stats.count300) >= 40000,
                // 400000 Keys
                47 => (stats.count50 + stats.count100 + stats.count300) >= 400000,
                // 4000000 Keys
                48 => (stats.count50 + stats.count100 + stats.count300) >= 4000000,
                // 40000000 Keys
                293 => (stats.count50 + stats.count100 + stats.count300) >= 40000000,

                _ => false
            };
        }
    }
}
