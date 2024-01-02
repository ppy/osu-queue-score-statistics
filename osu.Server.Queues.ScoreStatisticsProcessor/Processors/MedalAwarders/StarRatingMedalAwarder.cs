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
            return medal.achievement_id switch
            {
                // osu!standard
                // 1 star (Rising Star)
                55 => starRating >= 1 && starRating < 2,
                // 2 stars (Constellation Prize)
                56 => starRating >= 2 && starRating < 3,
                // 3 stars (Building Confidence)
                57 => starRating >= 3 && starRating < 4,
                // 4 stars (Insanity Approaches)
                58 => starRating >= 4 && starRating < 5,
                // 5 stars (These Clarion Skies)
                59 => starRating >= 5 && starRating < 6,
                // 6 stars (Above and Beyond)
                60 => starRating >= 6 && starRating < 7,
                // 7 stars (Supremacy)
                61 => starRating >= 7 && starRating < 8,
                // 8 stars (Absolution)
                62 => starRating >= 8 && starRating < 9,
                // 9 stars (Event Horizon)
                242 => starRating >= 9 && starRating < 10,
                // 10 stars (Phantasm)
                244 => starRating >= 10 && starRating < 11,

                // osu!taiko
                // 1 star (My First Don)
                71 => starRating >= 1 && starRating < 2,
                // 2 stars (Katsu Katsu Katsu)
                72 => starRating >= 2 && starRating < 3,
                // 3 stars (Not Even Trying)
                73 => starRating >= 3 && starRating < 4,
                // 4 stars (Face Your Demons)
                74 => starRating >= 4 && starRating < 5,
                // 5 stars (The Demon Within)
                75 => starRating >= 5 && starRating < 6,
                // 6 stars (Drumbreaker)
                76 => starRating >= 6 && starRating < 7,
                // 7 stars (The Godfather)
                77 => starRating >= 7 && starRating < 8,
                // 8 stars (Rhythm Incarnate)
                78 => starRating >= 8 && starRating < 9,

                // osu!catch
                // 1 star (A Slice Of Life)
                79 => starRating >= 1 && starRating < 2,
                // 2 stars (Dashing Ever Forward)
                80 => starRating >= 2 && starRating < 3,
                // 3 stars (Zesty Disposition)
                81 => starRating >= 3 && starRating < 4,
                // 4 stars (Hyperdash ON!)
                82 => starRating >= 4 && starRating < 5,
                // 5 stars (It's Raining Fruit)
                83 => starRating >= 5 && starRating < 6,
                // 6 stars (Fruit Ninja)
                84 => starRating >= 6 && starRating < 7,
                // 7 stars (Dreamcatcher)
                85 => starRating >= 7 && starRating < 8,
                // 8 stars (Lord of the Catch)
                86 => starRating >= 8 && starRating < 9,

                // osu!mania
                // 1 star (First Steps)
                87 => starRating >= 1 && starRating < 2,
                // 2 stars (No Normal Player)
                88 => starRating >= 2 && starRating < 3,
                // 3 stars (Impulse Drive)
                89 => starRating >= 3 && starRating < 4,
                // 4 stars (Hyperspeed)
                90 => starRating >= 4 && starRating < 5,
                // 5 stars (Ever Onwards)
                91 => starRating >= 5 && starRating < 6,
                // 6 stars (Another Surpassed)
                92 => starRating >= 6 && starRating < 7,
                // 7 stars (Extra Credit)
                93 => starRating >= 7 && starRating < 8,
                // 8 stars (Maniac)
                94 => starRating >= 8 && starRating < 9,

                _ => false
            };
        }

        private bool checkMedalFc(SoloScoreInfo score, Medal medal, double starRating)
        {
            return medal.achievement_id switch
            {
                // osu!standard
                // 1 star (Totality)
                63 => starRating >= 1 && starRating < 2,
                // 2 stars (Business As Usual)
                64 => starRating >= 2 && starRating < 3,
                // 3 stars (Building Steam)
                65 => starRating >= 3 && starRating < 4,
                // 4 stars (Moving Forward)
                66 => starRating >= 4 && starRating < 5,
                // 5 stars (Paradigm Shift)
                67 => starRating >= 5 && starRating < 6,
                // 6 stars (Anguish Quelled)
                68 => starRating >= 6 && starRating < 7,
                // 7 stars (Never Give Up)
                69 => starRating >= 7 && starRating < 8,
                // 8 stars (Aberration)
                70 => starRating >= 8 && starRating < 9,
                // 9 stars (Chosen)
                243 => starRating >= 9 && starRating < 10,
                // 10 stars (Unfathomable)
                245 => starRating >= 10 && starRating < 11,

                // osu!taiko
                // 1 star (Keeping Time)
                95 => starRating >= 1 && starRating < 2,
                // 2 stars (To Your Own Beat)
                96 => starRating >= 2 && starRating < 3,
                // 3 stars (Big Drums)
                97 => starRating >= 3 && starRating < 4,
                // 4 stars (Adversity Overcome)
                98 => starRating >= 4 && starRating < 5,
                // 5 stars (Demonslayer)
                99 => starRating >= 5 && starRating < 6,
                // 6 stars (Rhythm's Call)
                100 => starRating >= 6 && starRating < 7,
                // 7 stars (Time Everlasting)
                101 => starRating >= 7 && starRating < 8,
                // 8 stars (The Drummer's Throne)
                102 => starRating >= 8 && starRating < 9,

                // osu!catch
                // 1 star (Sweet And Sour)
                103 => starRating >= 1 && starRating < 2,
                // 2 stars (Reaching The Core)
                104 => starRating >= 2 && starRating < 3,
                // 3 stars (Clean Platter)
                105 => starRating >= 3 && starRating < 4,
                // 4 stars (Between The Rain)
                106 => starRating >= 4 && starRating < 5,
                // 5 stars (Addicted)
                107 => starRating >= 5 && starRating < 6,
                // 6 stars (Quickening)
                108 => starRating >= 6 && starRating < 7,
                // 7 stars (Supersonic)
                109 => starRating >= 7 && starRating < 8,
                // 8 stars (Dashing Scarlet)
                110 => starRating >= 8 && starRating < 9,

                // osu!mania
                // 1 star (Keystruck)
                111 => starRating >= 1 && starRating < 2,
                // 2 stars (Keying In)
                112 => starRating >= 2 && starRating < 3,
                // 3 stars (Hyperflow)
                113 => starRating >= 3 && starRating < 4,
                // 4 stars (Breakthrough)
                114 => starRating >= 4 && starRating < 5,
                // 5 stars (Everything Extra)
                115 => starRating >= 5 && starRating < 6,
                // 6 stars (Level Breaker)
                116 => starRating >= 6 && starRating < 7,
                // 7 stars (Step Up)
                117 => starRating >= 7 && starRating < 8,
                // 8 stars (Behind The Veil)
                118 => starRating >= 8 && starRating < 9,

                _ => false
            };
        }
    }
}
