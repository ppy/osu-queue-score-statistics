using System;
using System.Threading;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump
{
    [Command]
    [Subcommand(typeof(QueueCommands))]
    [Subcommand(typeof(BatchCommands))]
    public class Program
    {
        private static readonly CancellationTokenSource cts = new CancellationTokenSource();

        public static async Task Main(string[] args)
        {
            Console.CancelKeyPress += (_, e) =>
            {
                Console.WriteLine("Cancellation requested!");
                cts.Cancel();

                e.Cancel = true;
            };

            await CommandLineApplication.ExecuteAsync<Program>(args, cts.Token);
        }

        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp();
            return 1;
        }
    }
}
