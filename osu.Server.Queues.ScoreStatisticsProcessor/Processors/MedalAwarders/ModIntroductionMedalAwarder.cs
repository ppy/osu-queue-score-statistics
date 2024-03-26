using JetBrains.Annotations;
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

        public IEnumerable<Medal> Check(IEnumerable<Medal> medals, MedalAwarderContext context)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(context.Score.RulesetID);

            // Select score mods, ignoring certain mods that can be included in the combination for mod introduction medals
            Mod[] mods = context.Score.Mods.Select(m => m.ToMod(ruleset)).Where(m => !isIgnoredForIntroductionMedal(m)).ToArray();

            // Ensure the mod is the only one selected
            if (mods.Length != 1)
                yield break;

            // Ensure the mod is in the default configuration
            if (!mods[0].UsesDefaultConfiguration)
                yield break;

            foreach (var medal in medals)
            {
                if (checkMedal(context.Score, medal, mods[0]))
                    yield return medal;
            }
        }

        private bool checkMedal(SoloScoreInfo score, Medal medal, Mod mod)
        {
            switch (medal.achievement_id)
            {
                // Sudden Death (Finality)
                case 119:
                    return mod is ModSuddenDeath;

                // Perfect (Perfectionist)
                case 120:
                    return mod is ModPerfect;

                // Hard Rock (Rock Around The Clock)
                case 121:
                    return mod is ModHardRock;

                // Double Time (Time And A Half)
                case 122:
                    return mod is ModDoubleTime;

                // Nightcore (Sweet Rave Party)
                case 123:
                    return mod is ModNightcore;

                // Hidden (Blindsight)
                case 124:
                    return mod is ModHidden;

                // Flashlight (Are You Afraid Of The Dark?)
                case 125:
                    return mod is ModFlashlight;

                // Easy (Dial It Right Back)
                case 126:
                    return mod is ModEasy;

                // No Fail (Risk Averse)
                case 127:
                    return mod is ModNoFail;

                // Half Time (Slowboat)
                case 128:
                    return mod is ModHalfTime;

                // TODO: These medals are currently marked inoperable in osu-web-10.
                //       It may be desirable to set these medals up for lazer.
                // Relax
                // 129 => mod is ModRelax,
                // Autopilot
                // 130 => mod is OsuModAutopilot,
                // Spun Out (Burned Out)
                case 131:
                    return mod is OsuModSpunOut;

                default:
                    return false;
            }
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
