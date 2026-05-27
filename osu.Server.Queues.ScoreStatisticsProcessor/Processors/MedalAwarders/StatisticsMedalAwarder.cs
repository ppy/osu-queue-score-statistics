using JetBrains.Annotations;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class StatisticsMedalAwarder : IMedalAwarder
    {
        public bool RunOnFailedScores => false;

        public bool RunOnLegacyScores => false; // Legacy scores are handled by web-10.

        public IEnumerable<Medal> Check(IEnumerable<Medal> medals, MedalAwarderContext context)
        {
            foreach (var medal in medals)
            {
                if (checkMedal(medal, context))
                    yield return medal;
            }
        }

        private bool checkMedal(Medal medal, MedalAwarderContext context)
        {
            var score = context.Score;
            var stats = context.UserStats;

            Ruleset ruleset = ScoreStatisticsQueueProcessor.AVAILABLE_RULESETS.Single(r => r.RulesetInfo.OnlineID == score.ruleset_id);

            // Automation mods should not count towards max combo statistic.
            bool isAutomationMod = score.ScoreData.Mods.Select(m => m.ToMod(ruleset)).Any(m => m.Type == ModType.Automation);

            switch (medal.achievement_id)
            {
                // osu!standard
                // 500 Combo
                case 1:
                    return !isAutomationMod && score.max_combo >= 500;

                // 750 Combo
                case 3:
                    return !isAutomationMod && score.max_combo >= 750;

                // 1000 Combo
                case 4:
                    return !isAutomationMod && score.max_combo >= 1000;

                // 2000 Combo
                case 5:
                    return !isAutomationMod && score.max_combo >= 2000;

                // 5000 Plays
                case 20:
                    return stats.playcount >= 5000;

                // 15000 Plays
                case 21:
                    return stats.playcount >= 15000;

                // 25000 Plays
                case 22:
                    return stats.playcount >= 25000;

                // 50000 Plays
                case 28:
                    return stats.playcount >= 50000;

                // osu!taiko
                // 30000 Drum Hits
                case 31:
                    return (stats.count50 + stats.count100 + stats.count300) >= 30000;

                // 300000 Drum Hits
                case 32:
                    return (stats.count50 + stats.count100 + stats.count300) >= 300000;

                // 3000000 Drum Hits
                case 33:
                    return (stats.count50 + stats.count100 + stats.count300) >= 3000000;

                // 30000000 Drum Hits
                case 291:
                    return (stats.count50 + stats.count100 + stats.count300) >= 30000000;

                // osu!catch
                // Catch 20000 Fruits
                case 13:
                    return (stats.count50 + stats.count100 + stats.count300) >= 20000;

                // Catch 200000 Fruits
                case 23:
                    return (stats.count50 + stats.count100 + stats.count300) >= 200000;

                // Catch 2000000 Fruits
                case 24:
                    return (stats.count50 + stats.count100 + stats.count300) >= 2000000;

                // Catch 20000000 Fruits
                case 292:
                    return (stats.count50 + stats.count100 + stats.count300) >= 20000000;

                // osu!mania
                // 40000 Keys
                case 46:
                    return (stats.count50 + stats.count100 + stats.count300) >= 40000;

                // 400000 Keys
                case 47:
                    return (stats.count50 + stats.count100 + stats.count300) >= 400000;

                // 4000000 Keys
                case 48:
                    return (stats.count50 + stats.count100 + stats.count300) >= 4000000;

                // 40000000 Keys
                case 293:
                    return (stats.count50 + stats.count100 + stats.count300) >= 40000000;

                default:
                    return false;
            }
        }
    }
}
