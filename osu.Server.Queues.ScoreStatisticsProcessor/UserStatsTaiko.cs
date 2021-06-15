using Dapper.Contrib.Extensions;

namespace osu.Server.Queues.ScoreStatisticsProcessor
{
    [Table("osu_user_stats_taiko")]
    public class UserStatsTaiko : UserStats
    {
    }
}
