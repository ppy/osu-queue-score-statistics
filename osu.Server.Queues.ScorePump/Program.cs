using McMaster.Extensions.CommandLineUtils;

namespace osu.Server.Queues.ScorePump
{
    [Command]
    [Subcommand(typeof(PumpTestDataCommand))]
    [Subcommand(typeof(PumpAllScores))]
    public class Program
    {
        public static void Main(string[] args)
        {
            CommandLineApplication.Execute<Program>(args);
        }

        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp();
            return 1;
        }
    }
}
