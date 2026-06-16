# Сборка из корня репозитория:
#   docker build -f backend/AwardsFerm.Worker/Dockerfile -t awardsferm-worker .

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY AwardsFerm.slnx ./
COPY src/AwardsFerm.Core/AwardsFerm.Core.csproj src/AwardsFerm.Core/
COPY src/AwardsFerm.Infrastructure/AwardsFerm.Infrastructure.csproj src/AwardsFerm.Infrastructure/
COPY src/AwardsFerm.Worker/AwardsFerm.Worker.csproj src/AwardsFerm.Worker/
RUN dotnet restore src/AwardsFerm.Worker/AwardsFerm.Worker.csproj
COPY src/AwardsFerm.Core/ src/AwardsFerm.Core/
COPY src/AwardsFerm.Infrastructure/ src/AwardsFerm.Infrastructure/
COPY src/AwardsFerm.Worker/ src/AwardsFerm.Worker/
RUN dotnet publish src/AwardsFerm.Worker/AwardsFerm.Worker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/playwright/dotnet:v1.49.0-jammy AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN apt-get update && apt-get install -y --no-install-recommends xvfb curl \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8081
ENV BROWSER_HEADLESS=true
EXPOSE 8081
# Headless по умолчанию (Linux VPS). BROWSER_HEADLESS=false — запуск Xvfb для headed.
ENTRYPOINT ["sh", "-c", "if [ \"$BROWSER_HEADLESS\" = \"false\" ]; then export DISPLAY=:99; Xvfb :99 -screen 0 1920x1080x24 & fi; exec dotnet AwardsFerm.Worker.dll"]
