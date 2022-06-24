using System;
using System.Threading;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump
{
    [Command]
    [Subcommand(typeof(PumpTestDataCommand))]
    [Subcommand(typeof(PumpAllScores))]
    [Subcommand(typeof(WatchNewScores))]
    [Subcommand(typeof(ClearQueue))]
    [Subcommand(typeof(ImportHighScores))]
    public class Program
    {
        private static readonly CancellationTokenSource cts = new CancellationTokenSource();

        public static void Main(string[] args)
        {
            Console.CancelKeyPress += delegate
            {
                Console.WriteLine("Cancellation requested!");
                cts.Cancel();
            };

            CommandLineApplication.ExecuteAsync<Program>(args, cts.Token);
        }

        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp();
            return 1;
        }
    }
}
