// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    public static class AppSettings
    {
        /// <summary>
        /// The endpoint to query beatmap difficulties from.
        /// </summary>
        public static readonly string DIFFCALC_ENDPOINT;

        static AppSettings()
        {
            DIFFCALC_ENDPOINT = Environment.GetEnvironmentVariable("DIFFCALC_ENDPOINT") ?? "http://localhost:5001/attributes";
        }
    }
}
