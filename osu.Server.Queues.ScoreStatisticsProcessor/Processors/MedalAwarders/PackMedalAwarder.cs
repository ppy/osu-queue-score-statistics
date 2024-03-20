// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using Dapper;
using JetBrains.Annotations;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Processors.MedalAwarders
{
    [UsedImplicitly]
    public class PackMedalAwarder : IMedalAwarder
    {
        public bool RunOnFailedScores => false;

        public IEnumerable<Medal> Check(IEnumerable<Medal> medals, MedalAwarderContext context)
        {
            // Do a global check to see if this beatmapset is contained in *any* pack.
            var validPacksForBeatmapSet = context.Connection.Query<int>("SELECT pack_id FROM osu_beatmappacks_items WHERE beatmapset_id = @beatmapSetId", new
            {
                beatmapSetId = context.Score.Beatmap!.OnlineBeatmapSetID,
            }, transaction: context.Transaction);

            foreach (var medal in medals)
            {
                if (checkMedal(context, medal, validPacksForBeatmapSet))
                    yield return medal;
            }
        }

        private bool checkMedal(MedalAwarderContext context, Medal medal, IEnumerable<int> validPacksForBeatmapSet)
        {
            switch (medal.achievement_id)
            {
                // Pass all songs in Video Game Pack vol.1
                case 7:
                    return checkPack(40);

                // Pass all songs in Rhythm Game Pack vol.1
                case 8:
                    return checkPack(41);

                // Pass all songs in Internet! Pack vol.1
                case 9:
                    return checkPack(42);

                // Pass all songs in Anime Pack vol.1
                case 10:
                    return checkPack(43);

                // Pass all songs in Game Music Pack vol.2
                case 11:
                    return checkPack(48);

                // Pass all songs in Anime Pack vol.2
                case 12:
                    return checkPack(49);

                // Pass all songs in Internet! Pack vol.2
                case 18:
                    return checkPack(93);

                // Pass all songs in Rhythm Game Pack vol.2
                case 19:
                    return checkPack(94);

                // Pass all songs in Game Music Pack vol.3
                case 14:
                    return checkPack(70);

                // Pass all songs in Anime Pack vol.3
                case 25:
                    return checkPack(207);

                // Pass all songs in Internet! Pack vol.3
                case 27:
                    return checkPack(209);

                // Pass all songs in Rhythm Game Pack vol.3
                case 26:
                    return checkPack(208);

                // Pass all songs in Video Game Pack vol.4
                case 37:
                    return checkPack(364);

                // Pass all songs in Anime vol.4
                case 34:
                    return checkPack(363);

                // Pass all songs in rhythm vol.4
                case 35:
                    return checkPack(365);

                // Pass all songs in internet vol.4
                case 36:
                    return checkPack(366);

                // Spotlights January/February 2017 (1186-1189 packid)
                case 162:
                    return checkPack(1186) || checkPack(1187) || checkPack(1188) || checkPack(1189);

                // Spotlights March 2017 (1201-1204 packid)
                case 163:
                    return checkPack(1201) || checkPack(1202) || checkPack(1203) || checkPack(1204);

                // Spotlights April 2017 (1219-1222 packid)
                case 164:
                    return checkPack(1219) || checkPack(1220) || checkPack(1221) || checkPack(1222);

                // Spotlights May 2017 (1228-1232)
                case 165:
                    return checkPack(1228) || checkPack(1229) || checkPack(1230) || checkPack(1232);

                // Spotlights June 2017 (1244-1247)
                case 166:
                    return checkPack(1244) || checkPack(1245) || checkPack(1246) || checkPack(1247);

                // Spotlights July 2017 (1253-1256)
                case 167:
                    return checkPack(1253) || checkPack(1254) || checkPack(1255) || checkPack(1256);

                // Spotlights August 2017 (1264-1268)
                case 169:
                    return checkPack(1264) || checkPack(1265) || checkPack(1267) || checkPack(1268);

                // MOtOLOiD pack (pack_id 1284)
                case 179:
                    return checkPack(1284);

                // Spotlights September 2017 (1280-1283)
                case 180:
                    return checkPack(1280) || checkPack(1281) || checkPack(1282) || checkPack(1283);

                // Spotlights October 2017 (1292-1295)
                case 181:
                    return checkPack(1292) || checkPack(1293) || checkPack(1294) || checkPack(1295);

                // Spotlights November 2017 (1302-1305)
                case 182:
                    return checkPack(1302) || checkPack(1303) || checkPack(1304) || checkPack(1305);

                // Spotlights December 2017 (1331-1334)
                case 183:
                    return checkPack(1331) || checkPack(1332) || checkPack(1333) || checkPack(1334);

                // Spotlights January 2018 (1354-1357)
                case 184:
                    return checkPack(1354) || checkPack(1355) || checkPack(1356) || checkPack(1357);

                // Mappers Guild I, January 22~ 2018 (1365)
                case 185:
                    return checkPack(1365);

                // Spotlights February 2018 (1379-1382)
                case 186:
                    return checkPack(1379) || checkPack(1380) || checkPack(1381) || checkPack(1382);

                // Spotlights March 2018 (1405, 1407, 1408) - this month has no mania cuz ???
                case 187:
                    return checkPack(1405) || checkPack(1407) || checkPack(1408);

                // Spotlights April 2018 (1430, 1431, 1432, 1433)
                case 188:
                    return checkPack(1430) || checkPack(1431) || checkPack(1432) || checkPack(1433);

                // Cranky Pack (1437)
                case 189:
                    return checkPack(1437);

                // Mappers' Guild Pack 2 (1450)
                case 190:
                    return checkPack(1450);

                // Mappers' Guild Pack 3 (1480)
                case 191:
                    return checkPack(1480);

                // Summer Spotlights 2018 (1508-1511)
                case 205:
                    return checkPack(1508) || checkPack(1509) || checkPack(1510) || checkPack(1511);

                // Mappers' Guild Pack 4: Culprate (1535)
                case 206:
                    return checkPack(1535);

                // Seasonal Spotlights: Fall 2018 (1548-1551)
                case 207:
                    return checkPack(1548) || checkPack(1549) || checkPack(1550) || checkPack(1551);

                // Mappers' Guild Pack 5: HyuN (1581)
                case 208:
                    return checkPack(1581);

                // Seasonal Spotlights: Winter 2018 (1623-1626)
                case 209:
                    return checkPack(1623) || checkPack(1624) || checkPack(1625) || checkPack(1626);

                // Seasonal Spotlights: Spring 2019 (1670-1673)
                case 210:
                    return checkPack(1670) || checkPack(1671) || checkPack(1672) || checkPack(1673);

                // ICDD artist pack (1688)
                case 213:
                    return checkPack(1688);

                // Tieff MG artist pack (1649)
                case 214:
                    return checkPack(1649);

                // Seasonal Spotlights: Summer 2019 (1722-1725)
                case 215:
                    return checkPack(1722) || checkPack(1723) || checkPack(1724) || checkPack(1725);

                // Mappers Guild Pack III redux (1689)
                case 226:
                    return checkPack(1689);

                // Mappers' Guild Pack IV redux (1757)
                case 227:
                    return checkPack(1757);

                // Seasonal Spotlights: Autumn 2019 (1798-1801)
                case 228:
                    return checkPack(1798) || checkPack(1799) || checkPack(1800) || checkPack(1801);

                // Afterparty Featured Artist pack (1542)
                case 229:
                    return checkPack(1542);

                // Ben Briggs FA pack (1687)
                case 230:
                    return checkPack(1687);

                // Carpool Tunnel FA pack (1805)
                case 231:
                    return checkPack(1805);

                // Creo FA pack (1807)
                case 232:
                    return checkPack(1807);

                // cYsmix FA pack (1808)
                case 233:
                    return checkPack(1808);

                // Fractal Dreamers FA pack (1809)
                case 234:
                    return checkPack(1809);

                // LukHash FA pack (1758)
                case 235:
                    return checkPack(1758);

                // *namirin FA pack (1704)
                case 236:
                    return checkPack(1704);

                // onumi FA pack (1804)
                case 237:
                    return checkPack(1804);

                // The Flashbulb FA pack (1762)
                case 238:
                    return checkPack(1762);

                // Undead Corporation FA pack (1810)
                case 239:
                    return checkPack(1810);

                // Wisp X FA pack (1806)
                case 240:
                    return checkPack(1806);

                // Seasonal Spotlights: Winter 2020 (1896-1899)
                case 241:
                    return checkPack(1896) || checkPack(1897) || checkPack(1898) || checkPack(1899);

                // Camellia I Beatmap Pack (2051)
                case 246:
                    return checkPack(2051);

                // Camellia II Beatmap Pack (2053, CHALLENGE SET)
                case 247:
                    return checkPack(2053, true);

                // Celldweller Beatmap Pack (2040)
                case 248:
                    return checkPack(2040);

                // Cranky II Beatmap Pack (2049)
                case 249:
                    return checkPack(2049);

                // Cute Anime Girls FA Pack (2031)
                case 250:
                    return checkPack(2031);

                // ELFENSJoN beatmap pack (2047)
                case 251:
                    return checkPack(2047);

                // Hyper Potions beatmap pack (2037)
                case 252:
                    return checkPack(2037);

                // Kola Kid beatmap pack (2044)
                case 253:
                    return checkPack(2044);

                // LeaF beatmap pack (2039)
                case 254:
                    return checkPack(2039);

                // Panda Eyes beatmap pack (2043)
                case 255:
                    return checkPack(2043);

                // PUP beatmap pack (2048)
                case 256:
                    return checkPack(2048);

                // Ricky Montgomery beatmap pack (2046)
                case 257:
                    return checkPack(2046);

                // Rin beatmap pack (1759)
                case 258:
                    return checkPack(1759);

                // S3RL beatmap pack (2045)
                case 259:
                    return checkPack(2045);

                // Sound Souler beatmap pack (2038)
                case 260:
                    return checkPack(2038);

                // Teminite beatmap pack (2042)
                case 261:
                    return checkPack(2042);

                // VINXIS beatmap pack (2041)
                case 262:
                    return checkPack(2041);

                // Mappers' Guild Pack 5 (2032, diff allowed)
                case 263:
                    return checkPack(2032);

                // Mappers' Guild Pack 6 (2033, diff allowed)
                case 264:
                    return checkPack(2033);

                // Mappers' Guild Pack 7 (2034, CHALLENGE SET)
                case 265:
                    return checkPack(2034, true);

                // Mappers' Guild Pack 8 (2035, CHALLENGE SET)
                case 266:
                    return checkPack(2035, true);

                // Mappers' Guild Pack 9 (2036, CHALLENGE SET)
                case 267:
                    return checkPack(2036, true);

                // Touhou Pack (2457)
                case 282:
                    return checkPack(2457);

                // ginkiha Pack (2458)
                case 283:
                    return checkPack(2458);

                // MUZZ Pack (2459, challenge pack)
                case 284:
                    return checkPack(2459, true);

                // Vocaloid Pack (2481)
                case 288:
                    return checkPack(2481);

                // Maduk Pack (2482)
                case 289:
                    return checkPack(2482);

                // Aitsuki Nakuru Pack (2483)
                case 290:
                    return checkPack(2483);

                // Arabl'eyeS Pack (291-293 are taiko/fruits/mania hits progression), (2521, challenge pack)
                case 294:
                    return checkPack(2521, true);

                // Omoi Pack (2522)
                case 295:
                    return checkPack(2522);

                // Chill Pack (2523)
                case 296:
                    return checkPack(2523);

                // USAO Pack (2709, challenge pack)
                case 308:
                    return checkPack(2709, true);

                // Rohi Pack (2710)
                case 309:
                    return checkPack(2710);

                // Drum & Bass Pack (2711)
                case 310:
                    return checkPack(2711);

                // Project Loved: Winter 2021 (2712-2715)
                case 311:
                    return checkPack(2712) || checkPack(2713) || checkPack(2714) || checkPack(2715);

                // Project Loved: Spring 2022 (2716-2719)
                case 312:
                    return checkPack(2716) || checkPack(2717) || checkPack(2718) || checkPack(2719);

                // Project Loved: Summer 2022 (2720-2723)
                case 313:
                    return checkPack(2720) || checkPack(2721) || checkPack(2722) || checkPack(2723);

                // Project Loved: Autumn 2022 (2724-2727)
                case 314:
                    return checkPack(2724) || checkPack(2725) || checkPack(2726) || checkPack(2727);

                // Project Loved: Winter 2022 (2738, 2729-2731)
                case 315:
                    return checkPack(2738) || checkPack(2729) || checkPack(2730) || checkPack(2731);

                // Project Loved: Spring 2023 (2732-2735)
                case 316:
                    return checkPack(2732) || checkPack(2733) || checkPack(2734) || checkPack(2735);

                // Project Loved: Summer 2023 (3122-3125)
                case 325:
                    return checkPack(3122) || checkPack(3123) || checkPack(3124) || checkPack(3125);

                // in love with a ghost beatmap pack
                case 335:
                    return checkPack(3145);

                default:
                    return false;
            }

            bool checkPack(int packId, bool noReductionMods = false)
            {
                if (!validPacksForBeatmapSet.Contains(packId))
                    return false;

                string modsCriteria = string.Empty;
                string rulesetCriteria = string.Empty;

                if (noReductionMods)
                {
                    modsCriteria = @"AND json_search(data, 'one', 'EZ', null, '$.mods[*].acronym') IS NULL"
                                   + " AND json_search(data, 'one', 'NF', null, '$.mods[*].acronym') IS NULL"
                                   + " AND json_search(data, 'one', 'HT', null, '$.mods[*].acronym') IS NULL"
                                   + " AND json_search(data, 'one', 'DC', null, '$.mods[*].acronym') IS NULL"
                                   + " AND json_search(data, 'one', 'SO', null, '$.mods[*].acronym') IS NULL"
                                   // this conditional's goal is to exclude plays with unranked mods from query.
                                   // the reason why this is done in this roundabout way is that expressing the query in SQL is complicated otherwise,
                                   // and materialising the collection of all scores to check this C#-side is likely to be prohibitively expensive.
                                   + " AND s.pp IS NOT NULL";
                }

                // ensure the correct mode, if one is specified
                int? packRulesetId = context.Connection.QuerySingle<int?>($"SELECT playmode FROM `osu_beatmappacks` WHERE pack_id = {packId}", transaction: context.Transaction);

                if (packRulesetId != null)
                {
                    if (context.Score.RulesetID != packRulesetId)
                        return false;

                    rulesetCriteria = $"AND ruleset_id = {packRulesetId}";
                }

                // TODO: no index on (beatmap_id, user_id) may mean this is too slow.
                // note that the `preserve = 1` condition relies on the flag being set before score processing (https://github.com/ppy/osu-web/pull/10946).
                int completed = context.Connection.QuerySingle<int>(
                    "SELECT COUNT(distinct p.beatmapset_id)"
                    + "FROM osu_beatmappacks_items p "
                    + "JOIN osu_beatmaps b USING (beatmapset_id) "
                    + "JOIN scores s USING (beatmap_id)"
                    + $"WHERE s.user_id = {context.Score.UserID} AND s.passed = 1 AND s.preserve = 1 AND pack_id = {packId} {rulesetCriteria} {modsCriteria}", transaction: context.Transaction);

                int countForPack = context.Connection.QuerySingle<int>($"SELECT COUNT(*) FROM `osu_beatmappacks_items` WHERE pack_id = {packId}", transaction: context.Transaction);

                return completed >= countForPack;
            }
        }
    }
}
