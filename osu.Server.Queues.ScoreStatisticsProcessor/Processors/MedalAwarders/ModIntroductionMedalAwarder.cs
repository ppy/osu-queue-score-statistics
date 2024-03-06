using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using System.Collections.Generic;
using System.Linq;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class ModIntroductionMedalAwarder : IMedalAwarder
    {
        public bool RunOnFailedScores => false;

        public IEnumerable<Medal> Check(SoloScoreInfo score, UserStats userStats, IEnumerable<Medal> medals, MySqlConnection conn, MySqlTransaction transaction)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.RulesetID);

            // Select score mods, ignoring certain mods that can be included in the combination for mod introduction medals
            Mod[] mods = score.Mods.Select(m => m.ToMod(ruleset)).Where(m => !isIgnoredForIntroductionMedal(m)).ToArray();

            // Ensure the mod is the only one selected
            if (mods.Length != 1)
                yield break;

            // Ensure the mod is in the default configuration
            if (!mods[0].UsesDefaultConfiguration)
                yield break;

            foreach (var medal in medals)
            {
                if (checkMedal(score, medal, mods[0]))
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

                // TODO: These medals are currently marked inoperable in osu-web-10.
                //       It may be desirable to set these medals up for lazer.
                // Relax
                // 129 => mod is ModRelax,
                // Autopilot
                // 130 => mod is OsuModAutopilot,

                // Spun Out (Burned Out)
                131 => mod is OsuModSpunOut,

                _ => false
            };
        }

        private bool isIgnoredForIntroductionMedal(Mod m)
        {
            switch (m)
            {
                // Allow classic mod
                case ModClassic:
                    return true;

                default:
                    return m.Type == ModType.System;
            }
        }
    }
}
