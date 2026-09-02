FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY . .

# Build-args transportados pelo docker build (docker-ghcr.yml).
# Estes valores substituem os fallbacks de Directory.Build.props quando
# o build corre num contexto sem .git/ (que é o caso do Docker build).
#
# Fonte canónica única: as variáveis do GitHub Actions (${{ github.sha }},
# ${{ github.run_number }}, ${{ github.event.repository.updated_at }}, e
# ${{ github.ref }}). Nenhum valor é definido à mão.
#
# Os defaults abaixo só servem para `docker build` manual sem -build-arg;
# nunca devem chegar a produção porque o workflow docker-ghcr.yml é o
# único path de publicação.
#
# Para tags, ${{ github.ref }} tem o formato "refs/tags/v0.1.1". Para
# push a branches, tem o formato "refs/heads/main". Em qualquer caso,
# o RUN abaixo normaliza para SemVer puro (sem prefixo de protocolo
# nem "v").
ARG M3uCrawlerVersion=0.0.0-dev
ARG M3uCrawlerCommitSha=unknown
ARG M3uCrawlerBuildNumber=0
ARG M3uCrawlerBuildDate=1970-01-01T00:00:00Z

# Normaliza M3uCrawlerVersion para SemVer puro:
#   refs/tags/v0.1.1   -> 0.1.1
#   refs/heads/main     -> main
#   develop/feature     -> develop/feature
#   v0.1.1-rc.1         -> 0.1.1-rc.1
# Strip os prefixos de protocolo Git refs/tags/, refs/heads/ (passados
# via ${{ github.ref }} no workflow) e o "v" opcional das tags SemVer.
# A normalização acontece em runtime no shell do RUN abaixo e é
# consumida pela mesma shell, porque cada RUN do Dockerfile corre numa
# camada isolada — variáveis shell não sobrevivem entre RUNs. Por isso
# restore+publish+envio de propriedades acontece tudo num único RUN.
RUN M3uCrawlerVersionSemver=$(echo "${M3uCrawlerVersion}" | sed -e 's|^refs/tags/||' -e 's|^refs/heads/||' -e 's|^v||') && \
    echo "Version: ${M3uCrawlerVersion} -> SemVer: ${M3uCrawlerVersionSemver}" && \
    dotnet restore m3uCrawler/m3uCrawler.csproj \
        /p:M3uCrawlerVersion=${M3uCrawlerVersionSemver} \
        /p:M3uCrawlerCommitSha=${M3uCrawlerCommitSha} \
        /p:M3uCrawlerBuildNumber=${M3uCrawlerBuildNumber} \
        /p:M3uCrawlerBuildDate=${M3uCrawlerBuildDate} && \
    dotnet publish m3uCrawler/m3uCrawler.csproj \
        -c Release \
        -o /app/publish \
        /p:UseAppHost=false \
        /p:M3uCrawlerVersion=${M3uCrawlerVersionSemver} \
        /p:M3uCrawlerCommitSha=${M3uCrawlerCommitSha} \
        /p:M3uCrawlerBuildNumber=${M3uCrawlerBuildNumber} \
        /p:M3uCrawlerBuildDate=${M3uCrawlerBuildDate}

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /data

COPY --from=build /app/publish /app

# The application is executed from /app but reads/writes runtime data from /data
# (output, session files, and optional wtelegram.config).
ENTRYPOINT ["dotnet", "/app/m3uCrawler.dll"]
