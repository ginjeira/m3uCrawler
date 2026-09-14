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

- **Linguagem e framework**: C# em .NET 9.0 (`m3uCrawler/m3uCrawler.csproj`, `m3uCrawler.Tests/m3uCrawler.Tests.csproj`). Não há `global.json`. `Directory.Build.props`/`Directory.Build.targets` existem na raiz e são a fonte canónica do versionamento SemVer e dos metadados de build (consumidos por `BuildInfo` e pelo Dockerfile — ver `m3uCrawler/Build/BuildInfo.cs`).
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
  → `PlaylistManagerService.SaveToM3uPlaylist` / `SaveToJsonReport`
  → `DispatcharrSyncService.RunAsync` (opcional, gated por `dispatcharr_enabled=true` em `wtelegram.config`).

### Dispatcharr sync (camada opcional)

- A integração com **Dispatcharr** é uma extensão pós-playlist e é **opt-in**: lê `dispatcharr_enabled` do `wtelegram.config`. Quando ausente ou `false`, é um no-op (zero HTTP, zero ficheiros extra).
- Quando activa, **default `dispatcharr_dry_run=true`**: o fluxo `Generate → Validate → Apply` produz `output/dispatcharr_plan_<ts>.json` e `output/dispatcharr_report_<ts>.json` sem chamadas HTTP de escrita. Só passa a mutar o Dispatcharr quando o utilizador coloca `dispatcharr_dry_run=false`.
- A camada de **matching** (`Services/Matching/*`) é **pura**: não tem dependências HTTP e é totalmente testável sem Dispatcharr. `IChannelMatcher.BuildPlan(...)` aceita listas de DTOs e devolve um `MatchPlan` determinístico (mesmo input → JSON byte-idêntico, controlado por `nowUtc` injetável).
- O `MatchPlan` é o **contrato intermédio** entre matching e apply: pode ser revisto, serializado (`MatchPlanSerializer`) e aplicado independentemente (`DispatcharrSyncService.ApplyAsync`).
- Casos ambíguos (`MatchBand.Ambiguous`) **nunca são aplicados automaticamente**: ficam `SyncOutcome.Ambiguous` no plano e contam em `report.AmbiguousDecisions`.
- Credenciais: `dispatcharr_api_key`, `dispatcharr_username` e `dispatcharr_password` vivem **apenas** em `wtelegram.config`, que está fora do artefacto `package.yml` e do git (já invariant). Nunca passam por `JsonSerializer.Serialize` directo — o `MatchPlanSerializer.SanitizeForSerialization` aplica `CredentialSanitizer.SanitizeUrl` ao campo `streamUrl`.
- Streams criadas pelo crawler usam `is_custom=true` no Dispatcharr, para que os campos `name/url/tvg_id` fiquem editáveis e não sejam varridos pelo `stale_stream_days` da conta M3U externa.
- Streams em modo leitura (`is_custom=false`, com `m3u_account` definido) têm `name/url/tvg_id/channel_group` **read-only** no Dispatcharr — não tentamos editá-las.

### Invariantes que **não** devem ser quebradas sem aprovação explícita

- `AnalyzePlaylist` é o **fast-reject** por país (≥3 famílias canónicas distintas). `ValidateStreams` é o **gate per-stream**. Ambos usam matching por tokens (não `string.Contains` sobre o conteúdo bruto).
- O fallback por `group-title` em `ValidateStreams` aceita apenas tokens de categoria explícitos do país (`pt` → `portugal`, `pt`, `🇵🇹`, etc.). Não transforma qualquer `group-title` em aprovação.
- `CredentialSanitizer.SanitizeUrl` e `SanitizeM3uContent` aplicam-se a **todos** os pontos de saída: consola, `RunReport`, JSONs de relatório, preview do dashboard, mensagens de erro. **Nunca** passar uma URL Xtream com credenciais a `Console.WriteLine`, `JsonSerializer.Serialize`, `SaveToJsonReport` ou ao endpoint de preview sem sanitizar.
- A playlist M3U funcional (`output/playlist.m3u`, `output/playlist_temp.m3u`, `GET /api/playlist`) preserva URLs Xtream reais — é o único artefacto onde credenciais são intencionais. Endpoints de **preview** usam sanitização.
- O modo manutenção (`--telegram-maintain`) **nunca** apaga `playlist.m3u` por ausência de novos candidatos: `MergeStreams(stillWorkingMain, [])` devolve `stillWorkingMain`.
- O `HttpListener` do dashboard (`--web`/`--web-port`/`--web-token`) é arrancado no **top-level** de `Main` em `m3uCrawler/Program.cs:34-71`, **independente** da pipeline Telegram (que continua condicionada a `args.Contains("--telegram")` na linha 73). O dashboard **pode** ser iniciado de forma **standalone** (apenas `--web [--web-port N] [--web-token T]`) sem precisar de `--telegram` nem de `WTelegram.LoginAsync`. A autenticação opcional com `--web-token` usa `CryptographicOperations.FixedTimeEquals` para timing-attack safety.
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
- `Directory.Build.props`, `Directory.Build.targets` — fonte canónica do versionamento SemVer e dos metadados de build (consumidos por `m3uCrawler/Build/BuildInfo.cs` em runtime e pelo `Dockerfile` em build context). Alterações têm impacto no contrato `/api/version` ↔ OCI labels (ver `DockerBuildInfoContractTests`).

### Ficheiros de credenciais e dados (protegidos por convenção)

Estes ficheiros **nunca** devem ser editados, versionados, logados ou incluídos em commits/diffs:

- `wtelegram.config`
- `session.dat`
- `WTelegram.session`
- Quaisquer ficheiros com tokens, API keys, passwords reais.
- **Chaves Dispatcharr** (`dispatcharr_api_key`, `dispatcharr_username`, `dispatcharr_password`) adicionadas em `wtelegram.config` continuam a partilhar o mesmo invariante: nunca versionadas, nunca no artefacto `package.yml`, nunca em logs.

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
- **`docs/architecture/*`** — descreve a arquitectura existente do catálogo, matching, Dispatcharr sync e ownership. **Não** é ainda uma arquitectura normativa agregadora (cada doc trata um tema específico). Wave futura introduzirá `ARCHITECTURE.md` consolidando invariantes transversais.

### Documentação como critério de aceitação (Definition of Done)

Uma alteração **funcional** só está concluída quando:

1. **Código** implementado.
2. **Testes** actualizados e a passar.
3. **Documentação afectada** actualizada (`m3uCrawler/README.md` para API, `docs/architecture/*` para decisões, `DEPLOYMENT.md`/`OPERATIONS.md` para ops).
4. **`CHANGELOG.md`** actualizado (secção `[Unreleased]`) **apenas** se a alteração for funcional/relevante para release — refactors puramente internos ou alterações só documentais não obrigam nova entrada.
5. **`ROADMAP.md`** actualizado **apenas** se o estado de uma iniciativa muda (concluído, em curso, replaneado).
6. **Revisão final** (opcional: `/review`) antes do commit.

Esta regra evita simultaneamente:
- regressão documental silenciosa ("a implementação evoluiu, a doc ficou para trás");
- burocracia inútil (entradas de changelog/refactor interno).

**Não** contar o número exacto de testes como propriedade permanente da arquitectura. Quando o número for necessário, identificá-lo como estado volátil datado, e preferir o comando `dotnet test ... --no-build --nologo` como referência operacional. O mesmo se aplica a `SHA` actual, `digest` imutável da imagem, datas exactas de validação, versões de ferramentas: data-os quando necessário, não os espalhe pelo documento.

**Não** duplicar conteúdo entre `README.md`, `m3uCrawler/README.md`, `ROADMAP.md`, `CHANGELOG.md` e `docs/architecture/*` — preferir cross-references.

---

## 8. Princípios transversais

- **Não perder dados**. `/opt/m3ucrawler/runtime-data` (no servidor) é estado vivo. Qualquer operação de deployment deve preservar `wtelegram.config`, `session.dat`, `playlists/`, `output/` e históricos.
- **Não expor credenciais**. Em logs, relatórios, dashboard, documentação, mensagens de erro, e qualquer artefacto versionado.
- **Não duplicar pipelines**. Candidatos Xtream entram na mesma pipeline unificada dos restantes M3U (sem segunda pipeline paralela).
- **Não introduzir volumes Docker anónimos/nomeados** que substituam os bind mounts actuais. Os dados continuam a viver em `/opt/m3ucrawler/runtime-data`.
- **Não alterar nada fora do escopo do pedido**. Mudanças cosméticas não solicitadas devem ser propostas separadamente.

---

## 9. Setup local para testes (Windows + Docker Desktop)

> Esta secção documenta como **qualquer agente AI** (Kilo, Roo, etc.) deve configurar e usar um ambiente local equivalente à produção, **sem nunca tocar no servidor**. O setup foi estabelecido em 2026-09-14 e é estável; detalhes interactivos (autenticação Telegram) vivem em `.kilo/LOCAL_DEV.md`.

### 9.1 Quando usar este setup

- Desenvolver/testar alterações sem aceder ao servidor de produção.
- Validar comportamento de novos commits antes de PR.
- Autenticar uma nova sessão Telegram (`session.dat`) sem afectar a sessão de produção.
- Experimentar mudanças de pipeline (matching, validação, catalog) com isolamento total.

### 9.2 Restrições absolutas

Estas restrições aplicam-se a qualquer sessão AI que vá interagir com o setup local:

1. **NUNCA** correr `git add wtelegram.config`, `git add session.dat` ou equivalente. Já estão em `.gitignore`, mas reforço.
2. **NUNCA** mostrar conteúdo de `wtelegram.config`, `session.dat`, ou passwords 2FA em logs, screenshots, mensagens ou commits.
3. **NUNCA** correr `docker compose up -d` sem `-f docker-compose.local.yml`. O `docker-compose.yml` da raiz aponta para paths Linux e nome de container `m3ucrawler` (produção).
4. **NUNCA** correr `docker pull ghcr.io/ginjeira/m3ucrawler:latest` — o package é **privado** (owner `ginjeira` reporta 0 packages públicos; todos os endpoints `/v2/` GHCR devolvem 401). Esta é uma propriedade da conta, não falta de credenciais locais.
5. **NUNCA** apagar `C:\Users\ULSSJOSE\m3ucrawler\runtime-data\wtelegram.config` — é uma cópia byte-exact (320 B) do servidor. Para regenerar é preciso aceder ao servidor.

### 9.3 Componentes do setup

| Componente | Localização | Propósito |
|---|---|---|
| Runtime data (bind mount) | `C:\Users\ULSSJOSE\m3ucrawler\runtime-data\` | Substitui `/opt/m3ucrawler/runtime-data` do servidor |
| Imagem local | `m3ucrawler:local` | Construída a partir do Dockerfile do repo, **não** puxada de GHCR |
| Compose local | `docker-compose.local.yml` (raiz do repo, **.gitignored**) | Duas configurações via profiles: `default` (só dashboard) e `full` (com Telegram) |
| `.gitignore` adicional | entrada `docker-compose.local.yml` linha 30 | Impede versionamento acidental |

### 9.4 Comandos essenciais

**Validar que o setup está operacional** (sem afectar nada):

```powershell
docker ps --filter name=m3ucrawler --format '{{.Names}} {{.Status}} {{.Ports}}'
Test-Path C:\Users\ULSSJOSE\m3ucrawler\runtime-data\session.dat   # esperado: True
Test-Path C:\Users\ULSSJOSE\m3ucrawler\runtime-data\wtelegram.config   # esperado: True
docker images m3ucrawler:local --format '{{.Repository}}:{{.Tag}} {{.Size}}'
```

**Reconstruir a imagem local** após mudanças no código (sem aceder ao GHCR):

```powershell
cd C:\Users\ULSSJOSE\Repos\m3uCrawler
$sha = git rev-parse HEAD
docker build -t m3ucrawler:local -f m3uCrawler/Dockerfile `
    --build-arg M3uCrawlerVersion=refs/heads/main `
    --build-arg M3uCrawlerCommitSha=$sha `
    --build-arg M3uCrawlerBuildNumber=0 `
    --build-arg M3uCrawlerBuildDate=$(Get-Date -Format 'o') `
    .
```

**Arrancar dashboard-only** (sem Telegram, para validação rápida):

```powershell
docker compose -f C:\Users\ULSSJOSE\Repos\m3uCrawler\docker-compose.local.yml up -d
# Dashboard em http://localhost:5000/
```

**Arrancar com Telegram** (autenticação interactiva):

```powershell
# Sempre em FOREGROUND com TTY, nunca em background, para ter controlo do stdin.
docker compose -f C:\Users\ULSSJOSE\Repos\m3uCrawler\docker-compose.local.yml --profile full run --rm m3ucrawler-telegram
# Dashboard em http://localhost:5001/ (porta 5001 para coexistir com o profile default)
# Após autenticação: Ctrl+P Ctrl+Q para detach sem matar.
```

### 9.5 Procedimento para primeira autenticação Telegram (após setup inicial)

Ver runbook completo em **`.kilo/LOCAL_DEV.md`** § "Primeira autenticação Telegram". Em resumo:

1. Apagar `session.dat` local se existir (estado corrompido de tentativas anteriores).
2. Cooling period 60s.
3. `docker compose ... --profile full run --rm m3ucrawler-telegram`.
4. Quando a app pedir `verification_code:`, digitar manualmente o código que o Telegram enviar (via app ou SMS).
5. Se pedir `password:`, digitar a password 2FA.
6. `Ctrl+P Ctrl+Q` para detach após ver `Autenticado como: <user>`.

### 9.6 Diagnóstico rápido

| Sintoma | Causa provável | Resolução |
|---|---|---|
| `docker pull ghcr.io/ginjeira/m3ucrawler:latest` → `denied` | Package privado, **não** falta de credenciais locais | Não tentar pull; usar `docker build -t m3ucrawler:local` |
| Dashboard em `http://localhost:5000/` falha | Profile errado, ou container não está no profile `default` | Ver `docker ps --filter name=m3ucrawler` |
| `PHONE_CODE_INVALID` em loop sem attach | `session.dat` local corrompido, **ou** stdin a receber input externo | Apagar `session.dat`, esperar 60s, foreground com `-it` |
| `docker compose ... up -d` falha com "no service selected" | Profiles mal seleccionados | Usar `docker compose --profile full` explicitamente |
| `/api/version` mostra versão diferente do `git rev-parse HEAD` | Imagem `m3ucrawler:local` desactualizada | Re-`docker build` com o SHA actual |

### 9.7 Não duplicar este setup noutro local do repo

Esta secção em `AGENTS.md` é a **referência canónica** para qualquer agente AI. O runbook detalhado está em `.kilo/LOCAL_DEV.md` (já `.gitignored` por convenção). **Não** duplicar em `m3uCrawler/README.md` nem em `DEPLOYMENT.md` — esses descrevem produção.
