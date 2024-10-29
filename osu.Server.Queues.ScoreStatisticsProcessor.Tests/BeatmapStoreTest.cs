// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Catch.Mods;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mania.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Taiko;
using osu.Game.Rulesets.Taiko.Mods;
using osu.Server.Queues.ScoreStatisticsProcessor.Stores;
using Xunit;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Tests
{
    public class BeatmapStoreTest
    {
        private static readonly HashSet<Type> osu_ranked_mods = new HashSet<Type>
        {
            typeof(OsuModNoFail),
            typeof(OsuModEasy),
            typeof(OsuModTouchDevice),
            typeof(OsuModHidden),
            typeof(OsuModHardRock),
            typeof(OsuModSuddenDeath),
            typeof(OsuModDoubleTime),
            typeof(OsuModHalfTime),
            typeof(OsuModNightcore),
            typeof(OsuModFlashlight),
            typeof(OsuModSpunOut),
            typeof(OsuModPerfect),
        };

        private static readonly HashSet<Type> taiko_ranked_mods = new HashSet<Type>
        {
            typeof(TaikoModNoFail),
            typeof(TaikoModEasy),
            typeof(TaikoModHidden),
            typeof(TaikoModHardRock),
            typeof(TaikoModSuddenDeath),
            typeof(TaikoModDoubleTime),
            typeof(TaikoModHalfTime),
            typeof(TaikoModNightcore),
            typeof(TaikoModFlashlight),
            typeof(TaikoModPerfect),
        };

        private static readonly HashSet<Type> catch_ranked_mods = new HashSet<Type>
        {
            typeof(CatchModNoFail),
            typeof(CatchModEasy),
            typeof(CatchModHidden),
            typeof(CatchModHardRock),
            typeof(CatchModSuddenDeath),
            typeof(CatchModDoubleTime),
            typeof(CatchModHalfTime),
            typeof(CatchModNightcore),
            typeof(CatchModFlashlight),
            typeof(CatchModPerfect),
        };

        private static readonly HashSet<Type> mania_ranked_mods = new HashSet<Type>
        {
            typeof(ManiaModNoFail),
            typeof(ManiaModEasy),
            typeof(ManiaModHidden),
            typeof(ManiaModSuddenDeath),
            typeof(ManiaModDoubleTime),
            typeof(ManiaModHalfTime),
            typeof(ManiaModNightcore),
            typeof(ManiaModFlashlight),
            typeof(ManiaModPerfect),
            typeof(ManiaModKey4),
            typeof(ManiaModKey5),
            typeof(ManiaModKey6),
            typeof(ManiaModKey7),
            typeof(ManiaModKey8),
            typeof(ManiaModFadeIn),
            typeof(ManiaModKey9),
            typeof(ManiaModMirror),
        };

        public static readonly object[][] RANKED_TEST_DATA =
        [
            [new OsuRuleset(), osu_ranked_mods],
            [new TaikoRuleset(), taiko_ranked_mods],
            [new CatchRuleset(), catch_ranked_mods],
            [new ManiaRuleset(), mania_ranked_mods],
        ];

        [Theory]
        [MemberData(nameof(RANKED_TEST_DATA))]
        public void TestLegacyModsMarkedAsRankedCorrectly(Ruleset ruleset, HashSet<Type> legacyModTypes)
        {
            var rulesetMods = ruleset.CreateAllMods();

            foreach (var mod in rulesetMods)
                Assert.Equal(legacyModTypes.Contains(mod.GetType()), BeatmapStore.IsRankedLegacyMod(mod));
        }
    }
}
