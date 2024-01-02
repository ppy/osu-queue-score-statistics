using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using System.Collections.Generic;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class ModIntroductionMedalAwarder : IMedalAwarder
    {
        public bool RunOnFailedScores => false;

        public IEnumerable<Medal> Check(SoloScoreInfo score, UserStats userStats, IEnumerable<Medal> medals, MySqlConnection conn, MySqlTransaction transaction)
        {
            // Ensure the mod is the only one selected
            if (score.Mods.Length != 1)
                yield break;

            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.RulesetID);
            Mod mod = score.Mods[0].ToMod(ruleset);

            // Ensure the mod is in the default configuration
            if (!mod.UsesDefaultConfiguration)
                yield break;

            foreach (var medal in medals)
            {
                if (checkMedal(score, medal, mod))
                    yield return medal;
            }
        }

        private bool checkMedal(SoloScoreInfo score, Medal medal, Mod mod)
        {
            return medal.achievement_id switch
            {
                // Sudden Death (Finality)
                119 => mod is ModSuddenDeath,
                // Perfect (Perfectionist)
                120 => mod is ModPerfect,
                // Hard Rock (Rock Around The Clock)
                121 => mod is ModHardRock,
                // Double Time (Time And A Half)
                122 => mod is ModDoubleTime,
                // Nightcore (Sweet Rave Party)
                123 => mod is ModNightcore,
                // Hidden (Blindsight)
                124 => mod is ModHidden,
                // Flashlight (Are You Afraid Of The Dark?)
                125 => mod is ModFlashlight,
                // Easy (Dial It Right Back)
                126 => mod is ModEasy,
                // No Fail (Risk Averse)
                127 => mod is ModNoFail,
                // Half Time (Slowboat)
                128 => mod is ModHalfTime,
                // Spun Out (Burned Out)
                131 => mod is OsuModSpunOut,

                _ => false
            };
        }
    }
}
