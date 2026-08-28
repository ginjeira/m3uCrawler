FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore m3uCrawler.csproj
RUN dotnet publish m3uCrawler.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /data

COPY --from=build /app/publish /app

# The application is executed from /app but reads/writes runtime data from /data
# (output, session files, and optional wtelegram.config).
ENTRYPOINT ["dotnet", "/app/m3uCrawler.dll"]
