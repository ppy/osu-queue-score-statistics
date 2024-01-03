using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Beatmap = osu.Server.Queues.ScoreStatisticsProcessor.Models.Beatmap;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class StarRatingMedalAwarder : IMedalAwarder
    {
        private BeatmapStore? beatmapStore;

        public bool RunOnFailedScores => false;

        public IEnumerable<Medal> Check(SoloScoreInfo score, UserStats userStats, IEnumerable<Medal> medals, MySqlConnection conn, MySqlTransaction transaction)
        {
            return checkAsync(score, medals, conn, transaction).Result;
        }

        private async Task<IEnumerable<Medal>> checkAsync(SoloScoreInfo score, IEnumerable<Medal> medals, MySqlConnection conn, MySqlTransaction transaction)
        {
            List<Medal> earnedMedals = new List<Medal>();

            Ruleset ruleset = LegacyRulesetHelper.GetRulesetFromLegacyId(score.RulesetID);
            Mod[] mods = score.Mods.Select(m => m.ToMod(ruleset)).ToArray();

            // Ensure the score isn't using any difficulty reducing mods
            if (mods.Any(x => x.IsDifficultyReductionMod()))
                return earnedMedals;

            // On mania, these medals cannot be triggered with key mods and Dual Stages
            if (score.RulesetID == 3 && mods.Any(isManiaDisallowedMod))
                return earnedMedals;

            try
            {
                beatmapStore ??= await BeatmapStore.CreateAsync(conn, transaction);

                Beatmap? beatmap = await beatmapStore.GetBeatmapAsync(score.BeatmapID, conn, transaction);
                if (beatmap == null)
                    return earnedMedals;

                // Make sure the map isn't Qualified or Loved, as those maps may occasionally have SR-breaking/aspire aspects
                if (beatmap.approved == BeatmapOnlineStatus.Qualified || beatmap.approved == BeatmapOnlineStatus.Loved)
                    return earnedMedals;

                // Get map star rating (including mods)
                APIBeatmap apiBeatmap = beatmap.ToAPIBeatmap();
                DifficultyAttributes? difficultyAttributes = await beatmapStore.GetDifficultyAttributesAsync(apiBeatmap, ruleset, mods, conn, transaction);
                if (difficultyAttributes == null)
                    return earnedMedals;

                // Award pass medals
                foreach (var medal in medals)
                {
                    if (checkMedalPass(score, medal, difficultyAttributes.StarRating))
                        earnedMedals.Add(medal);
                }

                // Check for FC and award FC medals if necessary
                if (score.MaxCombo == score.MaximumStatistics.Where(kvp => kvp.Key.AffectsCombo()).Sum(kvp => kvp.Value))
                {
                    foreach (var medal in medals)
                    {
                        if (checkMedalFc(score, medal, difficultyAttributes.StarRating))
                            earnedMedals.Add(medal);
                    }
                }
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"{score.ID} failed in StarRatingMedalAwarder with: {ex}");
            }

            return earnedMedals;
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

            return medal.achievement_id switch
            {
                // osu!standard
                // 9 stars (Event Horizon)
                242 => checkStarRating(starRating, 9),
                // 10 stars (Phantasm)
                244 => checkStarRating(starRating, 10),

                _ => false
            };
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

            return medal.achievement_id switch
            {
                // osu!standard
                // 9 stars (Chosen)
                243 => checkStarRating(starRating, 9),
                // 10 stars (Unfathomable)
                245 => checkStarRating(starRating, 10),

                _ => false
            };
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
