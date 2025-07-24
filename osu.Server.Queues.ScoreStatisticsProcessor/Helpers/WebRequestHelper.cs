// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using osu.Server.Queues.ScoreStatisticsProcessor.Models;
using Sentry;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class WebRequestHelper
    {
        private static readonly string? shared_interop_domain = Environment.GetEnvironmentVariable("SHARED_INTEROP_DOMAIN");
        private static readonly string? shared_interop_secret = Environment.GetEnvironmentVariable("SHARED_INTEROP_SECRET");

        private static readonly string? api_client_id = Environment.GetEnvironmentVariable("API_CLIENT_ID");
        private static readonly string? api_client_secret = Environment.GetEnvironmentVariable("API_CLIENT_SECRET");
        private static string? accessToken;
        private static DateTimeOffset? accessTokenExpiry;

        private static readonly HttpClient http = new HttpClient();

        public static HttpResponseMessage RunSharedInteropCommand(string command, string method = "GET", dynamic? postObject = null)
        {
            if (string.IsNullOrEmpty(shared_interop_domain))
            {
#if !DEBUG
                throw new InvalidOperationException($"Attempted legacy IO call without target domain specified ({command})");
#endif
                return null!;
            }

            if (string.IsNullOrEmpty(shared_interop_secret))
            {
#if !DEBUG
                throw new InvalidOperationException($"Attempted legacy IO call without secret set ({command}");
#endif
                return null!;
            }

            int retryCount = 3;

            retry:

            try
            {
                long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                string url = $"{shared_interop_domain}/_lio/{command}{(command.Contains('?') ? "&" : "?")}timestamp={time}";
                string signature = hmacEncode(url, Encoding.UTF8.GetBytes(shared_interop_secret));

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

                if (postObject != null)
                {
                    httpRequestMessage.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(postObject)));
                    httpRequestMessage.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
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

        /// <remarks>
        /// <para>
        /// Anyone reading this without context is probably thinking "what sort of abomination is this? isn't this all wrong???"
        /// The answer is that this is certainly weird, some may call it an abomination, but it <i>should</i> - given a specific set of circumstances that happens to be true - work.
        /// </para>
        /// <para>
        /// The simple way to retrieve a score's rank in the beatmap leaderboard would be to use the database.
        /// That cannot be done because the database is slow (>30s lookup times).
        /// The database cannot really be made any faster, it has a bunch of indexes on it already.
        /// </para>
        /// <para>
        /// Therefore, we want to leverage elasticsearch in some way.
        /// However, this score at the time this method is going to be called, <i>will not be</i> in elasticsearch, as that happens <i>after</i> <c>osu-queue-score-statistics</c> processing is done.
        /// That said, you don't actually <i>need</i> the score to be in ES at this point in time to retrieve its global rank in the beatmap leaderboard.
        /// All you need to determine <i>that</i> is the score's total and its ID, as those are the two determining factors of how many scores precede this one in the beatmap leaderboard.
        /// Add a 1 to that number and you get this score's rank.
        /// </para>
        /// <para>
        /// At this point there are two options: either hit ES directly from here, or hit <c>osu-web</c> which already does the same trick to retrieve global rank of a score in the beatmap leaderboard.
        /// The first approach was deemed undesirable, because it is preferred that there is a single connection point to ES in the infrastructure.
        /// Therefore, the <c>GET /scores/{id}</c> API endpoint should do the trick for the second approach.
        /// If this ever becomes a problem the proposition is to move the ES lookup logic to https://github.com/ppy/osu-global-rank-lookup-cache and have both web and this call into that component.
        /// </para>
        /// <para>
        /// Note that this working correctly also relies on the fact that <c>osu-web</c> API has lazer mode turned forcefully on with no way to toggle it off
        /// (see https://github.com/ppy/osu-web/blob/f46806bb81eb0d3b0807c70bf8e3dc13f0783ec9/app/Libraries/Search/ScoreSearchParams.php#L86-L88).
        /// </para>
        /// </remarks>
        public static int? GetScoreRankOnBeatmapLeaderboard(SoloScore score)
        {
            try
            {
                string? token = retrieveAccessToken();
                if (token == null)
                    return null;

                var requestMsg = new HttpRequestMessage(HttpMethod.Get, $"{shared_interop_domain}/api/v2/scores/{score.id}");
                requestMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var responseMsg = http.Send(requestMsg);

                if (!responseMsg.IsSuccessStatusCode || responseMsg.Content.ReadFromJsonAsync<ScoreResponse>().Result is not ScoreResponse scoreResponse)
                {
                    SentrySdk.CaptureMessage($"Failed to retrieve score rank from API: received status code {responseMsg.StatusCode}, content: {responseMsg.Content.ReadAsStringAsync().Result}",
                        SentryLevel.Warning);
                    return null;
                }

                return scoreResponse.rank_global;
            }
            catch (Exception e)
            {
                SentrySdk.CaptureException(e, scope => scope.Level = SentryLevel.Warning);
                return null;
            }
        }

        private static string? retrieveAccessToken()
        {
            if (string.IsNullOrEmpty(api_client_id) || string.IsNullOrEmpty(api_client_secret))
            {
#if !DEBUG
                throw new InvalidOperationException($"Attempted API call without client id and secret set!");
#endif
                return null;
            }

            if (accessToken != null && accessTokenExpiry != null && DateTimeOffset.Now <= accessTokenExpiry.Value.AddMinutes(-5))
                return accessToken;

            var requestMsg = new HttpRequestMessage(HttpMethod.Post, $"{shared_interop_domain}/oauth/token");
            requestMsg.Content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("client_id", api_client_id),
                new KeyValuePair<string, string>("client_secret", api_client_secret),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "public"),
            ]);

            var responseMsg = http.Send(requestMsg);

            if (!responseMsg.IsSuccessStatusCode
                || responseMsg.Content.ReadFromJsonAsync<TokenResponse>().Result is not TokenResponse tokenResponse
                || string.IsNullOrEmpty(tokenResponse.access_token))
            {
                SentrySdk.CaptureMessage($"Failed to retrieve access token for API call: received status code {responseMsg.StatusCode}, content: {responseMsg.Content.ReadAsStringAsync().Result}", SentryLevel.Warning);
                return null;
            }

            accessToken = tokenResponse.access_token;
            accessTokenExpiry = DateTimeOffset.Now.AddSeconds(tokenResponse.expires_in);
            return accessToken;
        }

        // ReSharper disable InconsistentNaming
        private class TokenResponse
        {
            public int expires_in { get; set; }
            public string access_token { get; set; } = string.Empty;
        }

        private class ScoreResponse
        {
            public int rank_global { get; set; }
        }
    }
}
