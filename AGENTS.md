# AGENTS.md — Instruções permanentes para agentes AI

> Este ficheiro é lido automaticamente por agentes AI (Kilo, Roo e similares) no início de cada sessão. Contém regras duradouras que devem ser respeitadas antes de qualquer alteração ao repositório. Não é um manual de administração — para procedimentos humanos ver `DEPLOYMENT.md`, `OPERATIONS.md`, `README.md` e `m3uCrawler/README.md`.

---

## 1. Processo recomendado

Qualquer alteração não-trivial segue, **por esta ordem**:

1. **Analisar** o pedido e o estado actual do repositório (ficheiros relevantes, memória do projecto, testes existentes).
2. **Plano**: produzir um plano concreto em modo `plan` antes de tocar em código. O plano deve identificar ficheiros a alterar, comandos de validação e critérios de aceitação.
3. **Aprovação**: submeter o plano ao utilizador. Não avançar para implementação sem aprovação explícita.
4. **Implementação**: alterar apenas os ficheiros listados no plano aprovado.
5. **Testes**: correr `dotnet build m3uCrawler.sln` e `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj` em Release. Se algum falhar, corrigir antes de continuar.
6. **Revisão**: opcionalmente usar `/review` para uma segunda passagem antes do commit.
7. **Commit**: o agente só faz commit se o utilizador o pedir explicitamente. Mensagens em PT, curtas, no estilo do `CHANGELOG.md`.

Para tarefas triviais (typo numa doc, mudança de uma flag) o plano pode ser dispensado, mas o resto do processo mantém-se.

---

## 2. Arquitectura e invariantes do projecto

- **Linguagem e framework**: C# em .NET 9.0 (`m3uCrawler/m3uCrawler.csproj`, `m3uCrawler.Tests/m3uCrawler.Tests.csproj`). Não há `global.json` nem `Directory.Build.props`/`Directory.Build.targets`.
- **Solução**: `m3uCrawler.sln` agrega `m3uCrawler/` (app) e `m3uCrawler.Tests/` (testes xUnit).
- **Ponto de entrada**: `m3uCrawler/Program.cs`. CLI, ciclo de manutenção, gravação do `RunReport` em `output/telegram_run_report.json`.
- **Pipeline canónico** (ver `m3uCrawler/README.md` para detalhe):
  `TelegramScraperService.SearchAndTestM3UInTelegramAsync`
  → `M3uCandidateDetector.DetectFromMessage`
  → download (URL ou anexo) com gate `#EXTM3U`
  → `M3uParserService.Parse`
  → `CountryChannelValidator.AnalyzePlaylist` (fast-reject por país, threshold 3)
  → `CountryChannelValidator.ValidateStreams` (gate per-stream)
  → `M3uTesterService.TestM3u8Stream`
  → `RunReport`
  → `PlaylistManagerService.SaveToM3uPlaylist` / `SaveToJsonReport`.

### Invariantes que **não** devem ser quebradas sem aprovação explícita

- `AnalyzePlaylist` é o **fast-reject** por país (≥3 famílias canónicas distintas). `ValidateStreams` é o **gate per-stream**. Ambos usam matching por tokens (não `string.Contains` sobre o conteúdo bruto).
- O fallback por `group-title` em `ValidateStreams` aceita apenas tokens de categoria explícitos do país (`pt` → `portugal`, `pt`, `🇵🇹`, etc.). Não transforma qualquer `group-title` em aprovação.
- `CredentialSanitizer.SanitizeUrl` e `SanitizeM3uContent` aplicam-se a **todos** os pontos de saída: consola, `RunReport`, JSONs de relatório, preview do dashboard, mensagens de erro. **Nunca** passar uma URL Xtream com credenciais a `Console.WriteLine`, `JsonSerializer.Serialize`, `SaveToJsonReport` ou ao endpoint de preview sem sanitizar.
- A playlist M3U funcional (`output/playlist.m3u`, `output/playlist_temp.m3u`, `GET /api/playlist`) preserva URLs Xtream reais — é o único artefacto onde credenciais são intencionais. Endpoints de **preview** usam sanitização.
- O modo manutenção (`--telegram-maintain`) **nunca** apaga `playlist.m3u` por ausência de novos candidatos: `MergeStreams(stillWorkingMain, [])` devolve `stillWorkingMain`.
- O `HttpListener` do dashboard só arranca **depois** de `WTelegram.LoginAsync` no branch `--telegram`, e só escuta no branch `--web` (verificado em `Program.cs` e `WebDashboardService.cs`). O dashboard não pode ser iniciado de forma standalone.
- `m3uCrawler/runtime-data/` é apenas um **placeholder commitado** (contém `channel-indicators.json`, `countries/pt.json`, `.gitkeep`). Em produção, `runtime-data` é montado como **bind mount** a partir de `/opt/m3ucrawler/runtime-data`, que vive fora do repositório e da imagem.

---

## 3. Ficheiros e componentes sensíveis

Os ficheiros abaixo **não** devem ser alterados sem necessidade directa, e qualquer alteração deve ser justificada no plano e aprovada pelo utilizador:

- `m3uCrawler/Program.cs` — ponto de entrada; alterações afectam CLI, ciclo de manutenção, gravação do `RunReport`.
- `m3uCrawler/Services/TelegramScraperService.cs` — pipeline Telegram.
- `m3uCrawler/Services/CountryChannelValidator.cs` — `AnalyzePlaylist` e `ValidateStreams` (invariantes de matching).
- `m3uCrawler/Services/CredentialSanitizer.cs` — regras de sanitização (invariante de segurança).
- `m3uCrawler/Services/WebDashboardService.cs` — autenticação do dashboard, endpoints JSON, sanitização de preview.
- `m3uCrawler/Services/PlaylistManagerService.cs` — escrita das playlists M3U/JSON (preserva credenciais só onde é correcto).
- `m3uCrawler/Services/M3uParserService.cs` — parser centralizado.
- `m3uCrawler/Services/M3uCandidateDetector.cs` — lógica de descoberta.
- `m3uCrawler/Models/RunReport.cs` e restantes em `m3uCrawler/Models/`.
- `Dockerfile`, `m3uCrawler/.dockerignore` — definem a imagem publicada em `ghcr.io/ginjeira/m3ucrawler`.
- `docker-compose.yml` — **fonte de verdade do deployment** (ver `DEPLOYMENT.md`). Mudanças têm impacto operacional directo.
- `.github/workflows/docker-ghcr.yml` — publica a imagem no GHCR.
- `.github/workflows/build-and-test.yml` — gate de CI em push/PR para `main`.
- `.github/workflows/package.yml` — artefacto `.tar.gz` para deploy manual.
- `.github/workflows/ci.yml` — **untracked, não tocar** (trabalho de outra pessoa).

### Ficheiros de credenciais e dados (protegidos por convenção)

Estes ficheiros **nunca** devem ser editados, versionados, logados ou incluídos em commits/diffs:

- `wtelegram.config`
- `session.dat`
- `WTelegram.session`
- Quaisquer ficheiros com tokens, API keys, passwords reais.

São já cobertos por `m3uCrawler/.dockerignore`. A pasta `m3uCrawler/runtime-data/` no repositório é apenas um placeholder; em produção é substituída por um bind mount.

---

## 4. Regras de alteração de código

- **Não inventar comportamento** que não esteja no código ou na documentação. Se algo não puder ser confirmado, assinalar a lacuna em vez de assumir.
- Preservar a **terminologia existente** do projecto (`AnalyzePlaylist`, `ValidateStreams`, `RunReport`, `telegram_run_report.json`, `CandidatePlaylist`, `RequiresContentVerification`, `CredentialSanitizer`, etc.).
- Não adicionar dependências novas sem aprovação explícita.
- Não renomear APIs públicas sem actualizar todos os consumidores (incluindo `WebDashboardService`, testes, `Program.cs`).
- Não trocar APIs preservadas por legacia. `CountryChannelValidator.ValidatePlaylist` permanece exposto (retrosscompatibilidade).
- Em `Release`, `dotnet build` deve continuar a dar **0 warnings, 0 errors**.

---

## 5. Regras de testes

- Suite: `m3uCrawler.Tests/m3uCrawler.Tests.csproj` (xUnit).
- Comando canónico: `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo`.
- Comando completo: `dotnet build m3uCrawler.sln --configuration Release --no-restore && dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo`.
- Qualquer alteração ao código de produção deve ser acompanhada de testes relevantes (positivos e negativos). Se não for razoável testar (ex.: I/O com rede real), justificar no plano.
- Não há testes de integração de rede; tudo é unitário e independente de infra-estrutura externa.
- O objectivo declarado é **0 warnings, 0 errors, todos os testes a passar** em Release.

---

## 6. Regras de git

- **Nunca** usar `git add .`. Usar sempre `git add <file>` explícito, apenas dos ficheiros do projecto. Razão: evita incluir artefactos (`m3uCrawler-main.zip`) e credenciais (`wtelegram.config`, `session.dat`).
- O agente **não** faz commits nem push sem instrução explícita do utilizador.
- Mensagens de commit em PT, curtas, no estilo do `CHANGELOG.md`. Formato recomendado: `tipo(âmbito): descrição`.
- Antes de qualquer commit, inspeccionar `git status`, `git diff` e `git log --oneline -10`. Verificar que credenciais e ficheiros de runtime não aparecem no diff.
- Branches de feature: usar prefixos como `feat/`, `fix/`, `chore/`, `docs/`.

---

## 7. Relação com a restante documentação

- **`README.md` (root)** — porta de entrada rápida. Aponta para a documentação detalhada.
- **`m3uCrawler/README.md`** — documentação técnica detalhada da aplicação (CLI, pipeline, validação, dashboard, parser, scenarios, testes). É a **referência primária** para o comportamento da aplicação. Os agentes devem consultá-la antes de assumir como a app se comporta.
- **`CHANGELOG.md`** — histórico de alterações em formato Keep a Changelog. Novas alterações significativas devem ser registadas aqui, secção `[Unreleased]`.
- **`DEPLOYMENT.md`** — runbook de deployment (instalação, update, rollback, bind mounts, imagem). Não duplicar em `AGENTS.md`.
- **`OPERATIONS.md`** — runbook operacional (logs, reports, diagnóstico, backups).
- **`ROADMAP.md`** — estado do projecto e direcção. Não é fonte de verdade técnica.
- **`STREAM_LIMIT_GUIDE.md`**, **`EXEMPLOS.md`** — documentos pré-pipeline; podem estar obsoletos. Em caso de dúvida, preferir `m3uCrawler/README.md`.
- **`CONTRIBUTING.md`** — guia de contribuição para humanos (issue, PR). As regras detalhadas para agentes vivem aqui em `AGENTS.md`; `CONTRIBUTING.md` deve apenas referenciar este ficheiro para o detalhe técnico.

---

## 8. Princípios transversais

- **Não perder dados**. `/opt/m3ucrawler/runtime-data` (no servidor) é estado vivo. Qualquer operação de deployment deve preservar `wtelegram.config`, `session.dat`, `playlists/`, `output/` e históricos.
- **Não expor credenciais**. Em logs, relatórios, dashboard, documentação, mensagens de erro, e qualquer artefacto versionado.
- **Não duplicar pipelines**. Candidatos Xtream entram na mesma pipeline unificada dos restantes M3U (sem segunda pipeline paralela).
- **Não introduzir volumes Docker anónimos/nomeados** que substituam os bind mounts actuais. Os dados continuam a viver em `/opt/m3ucrawler/runtime-data`.
- **Não alterar nada fora do escopo do pedido**. Mudanças cosméticas não solicitadas devem ser propostas separadamente.
