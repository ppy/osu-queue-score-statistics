using Dapper.Contrib.Extensions;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    [Table("osu_user_stats")]
    public class UserStatsOsu : UserStats
    {
    }
}
