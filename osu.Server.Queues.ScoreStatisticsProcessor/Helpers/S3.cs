// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;

namespace osu.Server.Queues.ScoreStatisticsProcessor.Helpers
{
    public static class S3
    {
        public static AmazonS3Client GetClient(RegionEndpoint? endpoint = null)
        {
            string s3Key = Environment.GetEnvironmentVariable("S3_KEY") ?? throw new InvalidOperationException("S3_KEY must be specified.");
            string s3Secret = Environment.GetEnvironmentVariable("S3_SECRET") ?? throw new InvalidOperationException("S3_SECRET must be specified.");

            return new AmazonS3Client(new BasicAWSCredentials(s3Key, s3Secret), new AmazonS3Config
            {
                CacheHttpClient = true,
                HttpClientCacheSize = 32,
                RegionEndpoint = endpoint ?? RegionEndpoint.USWest1,
                UseHttp = true,
                ForcePathStyle = true
            });
        }
    }
}
