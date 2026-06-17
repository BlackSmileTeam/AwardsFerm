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
RUN apt-get update && apt-get install -y --no-install-recommends xvfb x11vnc curl \
    && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://+:8081
ENV BROWSER_HEADLESS=true
ENV BROWSER_VNC=false
ENV BROWSER_VNC_PASSWORD=
ENV DISPLAY=:99
EXPOSE 8081
# Headless по умолчанию (Linux VPS). BROWSER_HEADLESS=false — запуск Xvfb для headed.
ENTRYPOINT ["sh", "-c", "if [ \"$BROWSER_HEADLESS\" = \"false\" ]; then Xvfb :99 -screen 0 1920x1080x24 -ac +extension RANDR & if [ \"$BROWSER_VNC\" = \"true\" ]; then if [ -n \"$BROWSER_VNC_PASSWORD\" ]; then x11vnc -display :99 -forever -shared -passwd \"$BROWSER_VNC_PASSWORD\" -listen 0.0.0.0 -rfbport 5900 & else x11vnc -display :99 -forever -shared -nopw -listen 0.0.0.0 -rfbport 5900 & fi; fi; fi; exec dotnet AwardsFerm.Worker.dll"]
