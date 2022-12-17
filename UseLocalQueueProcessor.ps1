# Run this script to use a local copy of osu-queue-processor rather than fetching it from nuget.
# It expects the osu-queue-processor directory to be at the same level as the osu-queue-score-statistics directory.


$CSPROJ="osu.Server.Queues.ScoreStatisticsProcessor/osu.Server.Queues.ScoreStatisticsProcessor.csproj"
$SLN="osu.Server.Queues.ScoreStatisticsProcessor.sln"

$DEPENDENCIES=@(
    "..\osu-queue-processor\osu.Server.QueueProcessor\osu.Server.QueueProcessor.csproj"
)


dotnet remove $CSPROJ package ppy.osu.Server.OsuQueueProcessor

dotnet sln $SLN add $DEPENDENCIES
dotnet add $CSPROJ reference $DEPENDENCIES
