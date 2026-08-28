FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /data

# The application is executed from /app but reads/writes runtime data from /data
# (output, session files, and optional wtelegram.config).
ENTRYPOINT ["dotnet", "/app/m3uCrawler.dll"]
