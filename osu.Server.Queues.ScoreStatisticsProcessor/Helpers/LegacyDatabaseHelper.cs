// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class LegacyDatabaseHelper
    {
        public static RulesetDatabaseInfo GetRulesetSpecifics(int rulesetId)
        {
            switch (rulesetId)
            {
                default:
                case 0:
                    return new RulesetDatabaseInfo(0, "osu", false);

                case 1:
                    return new RulesetDatabaseInfo(1, "taiko", true);

                case 2:
                    return new RulesetDatabaseInfo(2, "fruits", true);

                case 3:
                    return new RulesetDatabaseInfo(3, "mania", true);
            }
        }

        public class RulesetDatabaseInfo
        {
            public readonly string UsersTable;
            public readonly string ScoreTable;
            public readonly string HighScoreTable;
            public readonly string LeadersTable;
            public readonly string UserStatsTable;
            public readonly string ReplayTable;
            public readonly string LastProcessedPpUserCount;
            public readonly string LastProcessedPpScoreCount;

            public RulesetDatabaseInfo(int rulesetId, string rulesetIdentifier, bool legacySuffix)
            {
                string tableSuffix = legacySuffix ? $"_{rulesetIdentifier}" : string.Empty;

                // If using the dumps, set this environment variable to "sample_users".
                string usersTable = Environment.GetEnvironmentVariable("DB_USERS_TABLE") ?? "phpbb_users";
                string dbName = Environment.GetEnvironmentVariable("DB_NAME") ?? "osu";

                UsersTable = $"`{dbName}`.`{usersTable}`";
                ScoreTable = $"`{dbName}`.`osu_scores{tableSuffix}`";
                HighScoreTable = $"`{dbName}`.`osu_scores{tableSuffix}_high`";
                LeadersTable = $"`{dbName}`.`osu_leaders{tableSuffix}`";
                UserStatsTable = $"`{dbName}`.`osu_user_stats{tableSuffix}`";
                ReplayTable = $"`{dbName}`.`osu_replays{tableSuffix}`";
                LastProcessedPpUserCount = $"pp_last_user_id{tableSuffix}";
                LastProcessedPpScoreCount = $"pp_last_score_id{tableSuffix}";
            }
        }

        private static readonly string legacy_io_secret = Environment.GetEnvironmentVariable("LEGACY_IO_SECRET") ?? string.Empty;

        private static readonly HttpClient http = new HttpClient();

        public static HttpResponseMessage RunLegacyIO(string command, string method = "GET", dynamic? postObject = null)
        {
            if (string.IsNullOrEmpty(legacy_io_secret))
            {
#if !DEBUG
                throw new InvalidOperationException("Attempted to award medal with no legacy IO secret set");
#endif
                return null!;
            }

            int retryCount = 3;

            retry:

            try
            {
                long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                string url = $"https://osu.ppy.sh/_lio/{command}{(command.Contains('?') ? "&" : "?")}timestamp={time}";
                string signature = hmacEncode(url, Encoding.UTF8.GetBytes(legacy_io_secret));

#pragma warning disable SYSLIB0014
                var request = WebRequest.CreateHttp(url);
#pragma warning restore SYSLIB0014

                request.Method = method;

                var httpRequestMessage = new HttpRequestMessage
                {
                    RequestUri = new Uri(url),
                    Method = new HttpMethod(method),
                    Headers =
                    {
                        { "X-LIO-Signature", signature },
                        { "Accept", "application/json" },
                    },
                };

                switch (postObject)
                {
                    case string postString:
                        httpRequestMessage.Content = new StringContent(postString);
                        httpRequestMessage.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("plain/text");
                        break;

                    default:
                        httpRequestMessage.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(postObject)));
                        httpRequestMessage.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
                        break;
                }

                var response = http.SendAsync(httpRequestMessage).Result;

                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Failed with {response.StatusCode} ({response.Content.ReadAsStringAsync().Result})");

                if ((int)response.StatusCode >= 300)
                    throw new Exception($"Issue with legacy IO: {response.StatusCode} {response.ReasonPhrase}");

                return response;
            }
            catch (Exception e)
            {
                if (retryCount-- > 0)
                {
                    Console.WriteLine($"Legacy IO failed with {e}, retrying..");
                    Thread.Sleep(1000);
                    goto retry;
                }

                throw;
            }
        }

        private static string hmacEncode(string input, byte[] key)
        {
            byte[] byteArray = Encoding.ASCII.GetBytes(input);

            using (var myhmacsha1 = new HMACSHA1(key))
            {
                byte[] hashArray = myhmacsha1.ComputeHash(byteArray);
                return hashArray.Aggregate(string.Empty, (s, e) => s + $"{e:x2}", s => s);
            }
        }
    }
}
