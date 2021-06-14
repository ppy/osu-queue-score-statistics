using System;
using System.Threading;
using osu.Server.Queues.ScoreStatisticsProcessor;

namespace osu.Server.Queues.ScorePump
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var pumpQueue = new ScoreStatisticsProcessor.ScoreStatisticsProcessor();

            // TODO: add operational modes with cli arguments

            while (true)
            {
                // TODO: push meaningful scores.
                var scoreItem = new ScoreItem();
                Console.WriteLine($"Pumping {scoreItem}");

                pumpQueue.PushToQueue(scoreItem);
                Thread.Sleep(200);
            }
        }
    }
}
