FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build-env
WORKDIR /app

# Copy everything else and build
COPY . ./
RUN dotnet publish osu.Server.Queues.ScorePump -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:5.0
WORKDIR /app
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "osu.Server.Queues.ScorePump.dll"]
