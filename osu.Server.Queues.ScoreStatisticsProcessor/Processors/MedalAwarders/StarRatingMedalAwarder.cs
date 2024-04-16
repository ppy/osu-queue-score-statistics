using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JetBrains.Annotations;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class StarRatingMedalAwarder : IMedalAwarder
    {
        public bool RunOnFailedScores => false;

        public IEnumerable<Medal> Check(IEnumerable<Medal> medals, MedalAwarderContext context)
        {
            return checkAsync(medals, context).ToBlockingEnumerable();
        }

        private async IAsyncEnumerable<Medal> checkAsync(IEnumerable<Medal> medals, MedalAwarderContext context)
        {
            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(context.Score.RulesetID);
            Mod[] mods = context.Score.Mods.Select(m => m.ToMod(ruleset)).ToArray();

            // Ensure the score isn't using any difficulty reducing mods
            if (mods.Any(MedalHelpers.IsDifficultyReductionMod))
                yield break;

            // On mania, these medals cannot be triggered with key mods and Dual Stages
            if (context.Score.RulesetID == 3 && mods.Any(isManiaDisallowedMod))
                yield break;

            var beatmap = context.Score.Beatmap;
            if (beatmap == null)
                yield break;

            // Make sure the map isn't Qualified or Loved, as those maps may occasionally have SR-breaking/aspire aspects
            if (beatmap.Status == BeatmapOnlineStatus.Qualified || beatmap.Status == BeatmapOnlineStatus.Loved)
                yield break;

            // Get map star rating (including mods)
            DifficultyAttributes? difficultyAttributes = await context.BeatmapStore.GetDifficultyAttributesAsync(beatmap, ruleset, mods, context.Connection, context.Transaction);

            if (difficultyAttributes == null)
                yield break;

            // Award pass medals
            foreach (var medal in medals)
            {
                if (checkMedalPass(context.Score, medal, difficultyAttributes.StarRating))
                    yield return medal;
            }

            // Check for FC and award FC medals if necessary
            if (context.Score.MaxCombo == context.Score.MaximumStatistics.Where(kvp => kvp.Key.AffectsCombo()).Sum(kvp => kvp.Value))
            {
                foreach (var medal in medals)
                {
                    if (checkMedalFc(context.Score, medal, difficultyAttributes.StarRating))
                        yield return medal;
                }
            }
        }

        private bool checkMedalPass(SoloScoreInfo score, Medal medal, double starRating)
        {
            // osu!standard 1-8*
            if (checkMedalRange(55, medal.achievement_id, starRating))
                return true;

            // osu!taiko 1-8*
            // Has an exception for https://osu.ppy.sh/beatmapsets/2626#taiko/19990
            if (score.BeatmapID != 19990 && checkMedalRange(71, medal.achievement_id, starRating))
                return true;

            // osu!catch 1-8*
            if (checkMedalRange(79, medal.achievement_id, starRating))
                return true;

            // osu!mania 1-8*
            if (checkMedalRange(87, medal.achievement_id, starRating))
                return true;

            switch (medal.achievement_id)
            {
                // osu!standard
                // 9 stars (Event Horizon)
                case 242:
                    return checkStarRating(starRating, 9);

                // 10 stars (Phantasm)
                case 244:
                    return checkStarRating(starRating, 10);

                default:
                    return false;
            }
        }

        private bool checkMedalFc(SoloScoreInfo score, Medal medal, double starRating)
        {
            // osu!standard 1-8*
            if (checkMedalRange(63, medal.achievement_id, starRating))
                return true;

            // osu!taiko 1-8*
            // Has an exception for https://osu.ppy.sh/beatmapsets/2626#taiko/19990
            if (score.BeatmapID != 19990 && checkMedalRange(95, medal.achievement_id, starRating))
                return true;

            // osu!catch 1-8*
            if (checkMedalRange(103, medal.achievement_id, starRating))
                return true;

            // osu!mania 1-8*
            if (checkMedalRange(111, medal.achievement_id, starRating))
                return true;

            switch (medal.achievement_id)
            {
                // osu!standard
                // 9 stars (Chosen)
                case 243:
                    return checkStarRating(starRating, 9);

                // 10 stars (Unfathomable)
                case 245:
                    return checkStarRating(starRating, 10);

                default:
                    return false;
            }
        }

        // Checks for medals in a 1-8* range, which tend to be sequential IDs
        private bool checkMedalRange(int medalIdStart, int medalId, double starRating)
        {
            if (medalId < medalIdStart || medalId > medalIdStart + 7)
                return false;

            return checkStarRating(starRating, medalId - medalIdStart + 1);
        }

        private bool checkStarRating(double starRating, int expected)
            => starRating >= expected && starRating < (expected + 1);

        private bool isManiaDisallowedMod(Mod mod)
        {
            switch (mod)
            {
                case ManiaKeyMod:
                case ManiaModDualStages:
                    return true;

                default:
                    return false;
            }
        }
    }
}
