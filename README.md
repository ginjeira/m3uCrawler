# m3uCrawler

Crawler em C# (.NET 9) para descobrir, validar e testar playlists M3U/M3U8 em fontes públicas (incluindo Telegram), classificando-as por país e gerando playlists consolidadas com os streams funcionais.

> **Documentação completa:** ver [`m3uCrawler/README.md`](m3uCrawler/README.md).

## Visão geral

- Descoberta automática de candidatos a playlist em fontes públicas (modo Telegram e modo scan de domínio).
- Pipeline integrado: descoberta → aquisição de conteúdo → detecção M3U → parser → validação por país → extracção de streams → teste de streams → relatório.
- Validação por país baseada nos títulos dos canais extraídos dos `#EXTINF`, com threshold de 3 canais distintos e famílias canónicas (variantes como `RTP1` e `RTP 1` contam como uma única família).
- Modo manutenção (`--telegram-maintain`) que preserva os streams existentes quando não há novas descobertas.
- Relatório detalhado em `output/telegram_run_report.json`.
- Dashboard web com gestão de listas de canais por país e diagnóstico da última execução.

## Estado actual

- `dotnet build m3uCrawler.sln --configuration Release` → 0 warnings, 0 errors.
- `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release` → 977 testes, todos passados.
- Deployment em produção via **Docker Compose** com imagem `ghcr.io/ginjeira/m3ucrawler:latest`. Ver `DEPLOYMENT.md`.

## Início rápido

```bash
cd m3uCrawler
dotnet restore
dotnet build
dotnet run -- --telegram portugal --history-hours 360
```

Para um ciclo de manutenção contínuo:

```bash
dotnet run -- --telegram portugal --telegram-maintain --loop-hours 24 --max-streams 500 --history-hours 360
```

## Autenticação Telegram

A autenticação WTelegram lê `wtelegram.config` (na pasta actual ou junto ao executável). Exemplo de estrutura com placeholders — **nunca** colocar credenciais reais em documentação ou source control:

```text
api_id=YOUR_API_ID
api_hash=YOUR_API_HASH
phone_number=YOUR_PHONE_NUMBER
verification_code=ask
password=ask
```

Coloca-se normalmente em `m3uCrawler/runtime-data/wtelegram.config`. Os valores `ask` fazem com que o campo seja pedido interactivamente no terminal.

## Estrutura do repositório

- `m3uCrawler/` — projecto principal (.NET 9) com toda a lógica, CLI, dashboard e testes.
- `m3uCrawler.Tests/` — testes unitários xUnit.
- `docker-compose.yml` e `m3uCrawler/Dockerfile` — execução em contentor. **O `docker-compose.yml` é a fonte de verdade do deployment** (ver `DEPLOYMENT.md`).
- `.github/workflows/docker-ghcr.yml` — publicação da imagem em `ghcr.io/ginjeira/m3ucrawler`.
- `.github/workflows/build-and-test.yml` — gate de CI em push/PR para `main`.
- `.github/workflows/package.yml` — artefacto `.tar.gz` para deploy manual.
- Documentação:
  - `m3uCrawler/README.md` — documentação detalhada da aplicação (CLI, pipeline, validação, dashboard).
  - `DEPLOYMENT.md` — runbook de deployment (instalação, update, rollback, imagem, bind mounts).
  - `OPERATIONS.md` — runbook operacional (logs, reports, diagnóstico, backups).
  - `ROADMAP.md` — estado e direcção do projecto.
  - `AGENTS.md` — instruções permanentes para agentes AI.
  - `CHANGELOG.md` — histórico de alterações.

## Aviso legal

Software destinado apenas para fins educacionais e de pesquisa. Assegure-se de que tem permissão para aceder aos streams, respeite os direitos autorais e os termos de serviço, e use apenas conteúdo legal e autorizado.

## Licença

MIT. Ver `LICENSE`.
