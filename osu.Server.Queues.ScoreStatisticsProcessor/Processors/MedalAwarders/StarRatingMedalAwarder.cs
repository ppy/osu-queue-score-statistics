using JetBrains.Annotations;
using MySqlConnector;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Server.Queues.ScoreStatisticsProcessor.Helpers;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    [SuppressMessage("ReSharper", "MergeIntoPattern")]
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
            if (mods.Any(x => x is ModEasy || x is ModNoFail || x is ModHalfTime))
                return earnedMedals;

            try
            {
                beatmapStore ??= await BeatmapStore.CreateAsync(conn, transaction);

                Beatmap? beatmap = await beatmapStore.GetBeatmapAsync(score.BeatmapID, conn, transaction);
                if (beatmap == null)
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
            if (checkMedalRange(71, medal.achievement_id, starRating))
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
                242 => checkSR(starRating, 9),
                // 10 stars (Phantasm)
                244 => checkSR(starRating, 10),

                _ => false
            };
        }

        private bool checkMedalFc(SoloScoreInfo score, Medal medal, double starRating)
        {
            // osu!standard 1-8*
            if (checkMedalRange(63, medal.achievement_id, starRating))
                return true;

            // osu!taiko 1-8*
            if (checkMedalRange(95, medal.achievement_id, starRating))
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
                243 => checkSR(starRating, 9),
                // 10 stars (Unfathomable)
                245 => checkSR(starRating, 10),

                _ => false
            };
        }

        // Checks for medals in a 1-8* range, which tend to be sequential IDs
        private bool checkMedalRange(int medalIdStart, int medalId, double starRating)
        {
            if (medalId < medalIdStart || medalId > medalIdStart + 7)
                return false;

            return checkSR(starRating, medalId - medalIdStart + 1);
        }

        private bool checkSR(double starRating, int expected)
            => starRating >= expected && starRating < (expected + 1);
    }
}
