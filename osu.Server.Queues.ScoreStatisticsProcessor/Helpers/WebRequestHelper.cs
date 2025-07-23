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
    public static class WebRequestHelper
    {
        private static readonly string? shared_interop_domain = Environment.GetEnvironmentVariable("SHARED_INTEROP_DOMAIN");
        private static readonly string? shared_interop_secret = Environment.GetEnvironmentVariable("SHARED_INTEROP_SECRET");

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
    }
}
