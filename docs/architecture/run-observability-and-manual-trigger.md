# m3uCrawler — Run Observability, Manual Trigger & Scheduled Start Time

> Documento de plano. **Não é uma alteração ao código.**
> Fonte canónica do estado actual: `docs/IMPLEMENTATION_ROADMAP.md`
> (secções 11, 22, 30, 32.1, 32.4–32.10) e `m3uCrawler/README.md`.
> Esta versão (2026-09-11) acrescenta a **§16** a possibilidade de
> agendar a primeira execução para uma hora específica, mantendo
> `--loop-hours` como recorrência.
> Fonte normativa para regras transversais: `AGENTS.md`.

---

## 1. Contexto e motivação

O operador do m3uCrawler que arranca o crawler com
`--telegram-maintain --loop-hours 24` (ou semelhante) **não tem
forma de saber, a partir do Dashboard, se um ciclo está em
execução neste momento nem em que ponto se encontra**. O
Dashboard apenas mostra a última execução **completa**, através
dos endpoints:

- `GET /api/run-report` e `GET /api/run-report/summary`
  (lidos de `output/telegram_run_report.json`)
- `GET /api/history` (últimas 72h, `ImportHistoryEntry`)
- `GET /api/execution/{idx}` (detalhe de uma execução passada)

Verificações realizadas (2026-09-11):

- `m3uCrawler/Models/RunReport.cs:59-62` — campos `StartedAt`,
  `FinishedAt`, `DurationMs`, `Status` (string
  `"pending" | "running" | "completed"`).
- `m3uCrawler/Services/TelegramScraperService.cs:131-138` — o
  pipeline define `rep.Status = "running"` no início e mantém o
  `RunReport` em memória (`LastRunReport`); **só é persistido em
  `output/telegram_run_report.json` no fim, por
  `Program.SaveRunReportAsync`**.
- `m3uCrawler/Services/WebDashboardService.cs:287-298` —
  `/api/run-report` lê **apenas** o ficheiro JSON, que durante
  uma execução corresponde ao run anterior.
- `m3uCrawler/Services/DashboardMetrics.cs:53-60` — `DeriveRunStatus`
  deriva `ok` / `sem-streams` / `falhou` a partir do `Status` final.
- Não existe nenhum endpoint `/api/run`, `/api/run/status`,
  `/api/run/start`, nem mecanismo SSE/WS, nem `Console.SetOut`
  redireccionado, nem leitura de ficheiro de log de runtime
  (`grep -i 'trigger|run-now|start-run|/api/run|/api/log|log-stream|sse|EventSource|Console.SetOut'` em `WebDashboardService.cs` devolve 0 matches relevantes para stream/control).
- O dashboard pode correr standalone (`--web [--web-port N] [--web-token T]`, conforme `AGENTS.md §2`), mas **não** pode disparar execuções — apenas gerir configuração.

Lacunas funcionais identificadas:

1. **Não há visibilidade em tempo real sobre o estado de uma
   execução em curso** (running / idle / completed / failed).
2. **Não há percentagem de progresso nem fases intermédias**
   expostas durante a execução. O pipeline só comunica contadores
   finais via `RunReport`.
3. **Não há trigger manual** a partir do Dashboard. As
   execuções continuam a ser despoletadas exclusivamente pela
   CLI (`--telegram`, `--telegram-maintain`, `--bot`,
   `--scan-domain`).
4. **Não há stream de logs** do ciclo em curso (SSE / WS / tail
   de ficheiro).
5. **Não há forma de fixar a hora da primeira execução** quando
   se usa `--telegram-maintain --loop-hours N`. O ciclo actual
   faz `Task.Delay(TimeSpan.FromHours(loopHours))` a contar do
   fim do run anterior
   (`m3uCrawler/Program.cs:300-307`); se o processo arrancar
   às 14h37 e `--loop-hours 24`, o primeiro run é imediato e os
   seguintes às 14h37 do dia seguinte — o operador não pode
   escolher "todos os dias às 03:00 UTC".

Esta wave fecha 1–3 (secções §5–§14) **e** 5 (§16). O ponto 4
mantém-se fora do âmbito (justificação em §4.2).

Este plano descreve o desenho mínimo que fecha 1, 2 e 3, deixando
4 (live log tail) **fora do âmbito desta iteração** — pode ser
endereçado por uma wave posterior, dado que envolve política de
buffer/rotação de logs e uma nova superfície HTTP persistente
(SSE), que merecem tratamento separado. A wave também fecha o
gap 5 (hora fixa da primeira execução) — ver §16.

---

## 2. Objectivo
Dashboard e responder, **sem ler logs nem aceder ao servidor**:

```text
Existe um ciclo a correr agora?            → sim/não (badge)
Se sim, em que fase está?                  → fase corrente + etapa numerada
Há quanto tempo começou?                   → "há 4m 12s"
Quantas streams já foram testadas?         → 23 / 200 (estilo X/N)
Quantas faltam estimadamente?              → "~3m 30s restantes"
Posso iniciar um ciclo agora a partir daqui? → botão "Run now"
```

E, no fim do ciclo, o snapshot persistido em
`output/telegram_run_report.json` mantém-se a fonte canónica de
diagnóstico (sem mudança de contrato).

---

## 3. Princípios de desenho

Reutiliza os princípios transversais do projecto
(`AGENTS.md §2` e `IMPLEMENTATION_ROADMAP §2`):

- **Não perder dados.** O ficheiro de progresso é *aditivo* —
  durante o run coexiste com `telegram_run_report.json` (que
  continua a ser a fonte final). Em caso de crash, o ficheiro
  de progresso fica como "último estado observado" e é lido
  conservadoramente (ver §6.4 stale detection).
- **Não expor credenciais.** O snapshot de progresso regista
  contadores e nomes de fases. Não inclui URLs de streams,
  `RunReport.DiscoveredPlaylists[].Name`, nem mensagens de
  exceção com URLs. Se uma mensagem de fase trouxer URL, passa
  por `CredentialSanitizer.SanitizeUrl`.
- **Não duplicar pipelines.** O trigger manual invoca o mesmo
  `TelegramScraperService.SearchAndTestM3UInTelegramAsync`
  (ou o ciclo de manutenção `RunTelegramMaintenanceCycle` em
  `Program.cs`) que a CLI. Não há um runner paralelo.
- **Não inventar comportamento.** O trigger manual respeita os
  parâmetros activos do processo (config em
  `runtime-data/wtelegram.config`, `stream_validation_policy.json`,
  lista de países, etc.) e os invariantes do pipeline
  (`AGENTS.md §2`).
- **Dashboard como control plane.** Esta onda acrescenta um
  trigger manual **e** o estado em tempo real — combina
  naturalmente e a UI mostra-os em conjunto (ver §7).
- **Sanitização obrigatória** em qualquer artefacto novo
  (`CredentialSanitizer.SanitizeUrl` em URLs, `SanitizeM3uContent`
  em previews).

Compatibilidade:

- **CLI existente não muda.** `--telegram`, `--telegram-maintain`,
  `--bot`, `--scan-domain` continuam a funcionar sem alterações.
- **Contrato `RunReport` não muda.** O snapshot final escrito
  em `output/telegram_run_report.json` mantém o mesmo schema
  (camelCase, UTF-8). A wave **não** renomeia nem remove campos.
- **Dashboard standalone continua a funcionar.** O trigger
  manual é opt-in: se não houver pipeline Telegram configurada
  para correr (ex.: `--web` sem `--telegram` nem
  `--telegram-maintain`), o endpoint responde 409 com mensagem
  clara (ver §7.3).

---

## 4. Âmbito

### 4.1 In-scope

1. **Sink de progresso em disco** durante uma execução,
   escrito por instrumentação no `TelegramScraperService` e no
   ciclo de manutenção.
2. **Endpoint de estado** `GET /api/run/status` que devolve o
   estado actual (idle / running / completed / failed) +
   snapshot do progresso corrente.
3. **Endpoint de trigger** `POST /api/run/start` que despoleta
   um ciclo Telegram único, assíncrono, com lock anti-dupla
   execução.
4. **Integração com o `ScheduledJobRunner`** existente
   (PHASE 12 — `IMPLEMENTATION_ROADMAP §32.10`), para que o
   trigger manual funcione como mais um `IScheduledAction`
   programável, se for útil.
5. **UI mínima no Dashboard** (tab Overview / novo bloco "Run")
   com badge de estado, barra de progresso, botão "Run now",
   lista das últimas N execuções recentes.
6. **Sanitização** completa nos snapshots e payloads.
7. **Testes** unitários + HTTP via `WebDashboardHttpApiTests`
   pattern (já usado na PHASE 2 — `IMPLEMENTATION_ROADMAP §32.4`).

### 4.2 Out-of-scope (explicitamente)

- **Live log tail / SSE** (stream de stdout durante o ciclo).
  Decisão: não nesta wave. Justificação: requer política de
  rotação de logs e abre uma nova superfície HTTP persistente
  (long-lived connection) que merece um documento próprio.
  Pode ser wave própria que reutiliza o sink de progresso
  (`§5.1`) escrevendo também para um `runtime-data/last_run.log`
  com rotação.
- **Trigger para `--scan-domain` ou `--bot`** a partir do
  Dashboard. Apenas o ciclo Telegram principal
  (`SearchAndTestM3UInTelegramAsync`) e o ciclo de manutenção
  (`RunTelegramMaintenanceCycle`). As outras variantes são
  rarefeitas e têm superfície CLI própria.
- **Cancelamento de um run em curso.** O trigger manual apenas
  dispara. Cancelamento cooperativo via `CancellationToken`
  é viável, mas adiciona complexidade significativa (partilha
  de token entre request HTTP e Task em background, locking,
  UX de "Cancel"). Decisão: deferir para wave posterior;
  alinhar com o design da PHASE 12 (que também não cancela).
- **Múltiplas execuções em paralelo.** Apenas uma execução
  activa. Lock por processo (não distributed). Se vier um
  segundo `POST /api/run/start` enquanto uma corre, devolve
  409 com referência à execução em curso.
- **Autenticação dedicada do trigger.** Reutiliza o mesmo modelo
  do dashboard (`AGENTS.md §2`, secção "Modelo de segurança do
  dashboard"): se `--web-token` estiver configurado, todos os
  endpoints (incluindo o trigger) exigem o token. Sem token
  configurado, comportamento aberto (compatibilidade com uso
  local — `m3uCrawler/README.md §Dashboard`).

---

## 5. Desenho

### 5.1 Sink de progresso: `RunProgressSnapshot`

Novo modelo (`m3uCrawler/Models/RunProgressSnapshot.cs`)
representando o snapshot em disco:

```text
RunProgressSnapshot
    Status             // "idle" | "running" | "completed" | "failed" | "stale"
    Mode               // "telegram" | "telegram-maintain" | "scan-domain" | "bot"
    RunId              // GUID gerado por ciclo (permite correlacionar logs futuros)
    StartedAtUtc
    LastUpdatedAtUtc
    FinishedAtUtc?     // null enquanto running
    Phase              // string da fase corrente (ver §5.2)
    PhaseIndex         // 0-based; índice da fase corrente dentro de Phases
    Phases             // string[] ordenado, ex. ["discovery","parse","validate","merge","report"]
    PhaseStartedAtUtc  // quando a fase corrente começou
    Counts
        MessagesAnalyzed
        CandidatesFound
        PlaylistsDownloaded
        PlaylistsInvalid
        PlaylistsRejected
        CountryMatches
        StreamsExtracted
        StreamsAfterCountryFilter
        StreamsRejectedByCountry
        StreamsTested
        StreamsWorking
        StreamsFailed
    Estimate           // opcional
        EstimatedRemainingMs?  // ETA em ms; só presente quando há dados suficientes
        EstimatedTotalMs?      // total esperado pelo padrão histórico (mediana móvel)
    LastMessage        // string human-readable (sanitizada) da última transição
    Sanitized          // bool; sempre true — recordatório que o snapshot foi sanitizado
```

Notas:

- **Não** inclui URLs, títulos de canais, nem detalhes de
  stream. Só contadores. `LastMessage` é uma string curta
  (<200 chars) com a última fase concluída, ex.:
  `"validated 23/200 streams"`. Se a mensagem for montada com
  uma URL (improvável mas possível — p.ex. para indicar uma
  playlist que falhou o download), passa por
  `CredentialSanitizer.SanitizeUrl` antes de gravar.
- `Sanitized = true` é redundante com a invariante mas torna
  a leitura do ficheiro em auditoria mais óbvia.
- `Mode` permite ao operador distinguir, em runs de manutenção,
  se está num `--telegram` ad-hoc ou num ciclo `--telegram-maintain`.
- `RunId` é um `Guid.NewGuid()` por ciclo, escrito também em
  `telegram_run_report.json` como campo opcional (ver §5.6).
- `Phase`/`Phases`/`PhaseIndex` resolvem o problema da
  percentagem: a fase corrente é conhecida e o índice no array
  dá um progresso grosseiro "fase 3/5" enquanto os contadores
  dão o progresso fino dentro da fase (ver §5.2).

Ficheiro em disco: `output/telegram_run_progress.json`. Naming
alinhado com a convenção existente (`telegram_run_report.json`,
`telegram_maintain_report.json`).

Trato:

- Escrito **de forma atómica** (escrever para
  `*.tmp` + `File.Move(..., overwrite: true)`) — alinhado com
  o padrão já usado em `StreamValidationPolicyStore`
  (`m3uCrawler/Services/Validation/StreamValidationPolicyStore.cs`).
- Throttle de escrita: **no máximo uma vez por 250 ms** ou em
  cada mudança de fase (o que ocorrer primeiro). O objectivo é
  fornecer live updates sem I/O excessivo. Implementação:
  `Channel<DateTimeOffset>` interno ou coalescência por timer.
- Cada ciclo começa por **apagar** o ficheiro
  `telegram_run_progress.json` (de runs anteriores) no início
  do `RunTelegramMaintenanceCycle` (em `Program.cs`) ou no
  início de `SearchAndTestM3UInTelegramAsync`. O snapshot
  corrente só persiste enquanto o ciclo está vivo.

### 5.2 Fases do pipeline

Mapeamento `Phase` (string) ↔ ponto de instrumentação no código
existente:

| `Phase`          | Onde se regista hoje                                                                 | Onde se instrumenta                                                                                  |
|------------------|---------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------------|
| `discovery`      | `TelegramScraperService.SearchM3UInTelegramInternal` (`Services/TelegramScraperService.cs:135`) | Antes e depois desse método; `Counts.MessagesAnalyzed` + `Counts.CandidatesFound`                    |
| `parse`          | `for (ci…)` em `SearchAndTestM3UInTelegramAsync` (linhas 150–…)                       | Iteração por candidato; `Counts.PlaylistsDownloaded`, `Counts.PlaylistsInvalid`                       |
| `validate`       | mesmo `for` (teste de streams)                                                        | Iteração por stream testado; `Counts.StreamsTested/Working/Failed`                                   |
| `merge`          | `RunTelegramMaintenanceCycle` em `Program.cs` (apenas modo manutenção)                | `MergeStreams(...)`; apenas presente no modo `telegram-maintain`                                     |
| `report`         | `SaveRunReportAsync` em `Program.cs`                                                  | Última fase; marca `Status="completed"` e `FinishedAtUtc`                                            |

Regras:

- Em modo `--telegram` ad-hoc, `Phases = ["discovery","parse","validate","report"]`
  (4 fases). Em modo `--telegram-maintain`, `Phases = ["discovery","parse","validate","merge","report"]`
  (5 fases). Decisão tomada uma vez no início do run.
- A fase `discovery` tem peso 1; `parse` tem peso proporcional
  a `CandidatesFound` (ou 1 se 0); `validate` tem peso
  proporcional a `StreamsExtracted` (ou 1 se 0); `merge` tem
  peso fixo 1 (a merge é tipicamente rápida); `report` tem peso
  fixo 1.
- A "percentagem global" combinando fase + contadores é
  calculada no **cliente** (Dashboard JS), não no servidor —
  o servidor só emite contadores + fase corrente. Mantém o
  payload pequeno.

### 5.3 Lock anti-dupla execução

Novo modelo leve em `Program.cs` (ou em
`Services/Automation/RunCoordinator.cs`):

```text
class RunCoordinator
    bool IsRunning { get; }
    string? CurrentRunId { get; }
    DateTimeOffset? StartedAt { get; }
    Task RunAsync(Func<IRunProgressSink, CancellationToken, Task> body, string mode, CancellationToken ct)
```

Responsabilidades:

- Singleton dentro do processo (registo em `ServiceCollection`).
- `IsRunning` começa a `false`. `RunAsync` faz CAS
  (`Interlocked.CompareExchange`) para garantir que apenas um
  ciclo corre de cada vez.
- Se já houver um run activo, `RunAsync` lança
  `RunAlreadyInProgressException` (mapeada para HTTP 409).
- O `body` recebe um `IRunProgressSink` (interface injetada)
  que encapsula a escrita do `RunProgressSnapshot` em disco
  (§5.1) com throttle automático.
- `CancellationToken` é passado adiante para `body` — alinha
  com o padrão já existente no
  `M3uTesterService.RunAsync(urls, overrideOptions?, ct?)`.

Integração:

- `Program.RunTelegramMaintenanceCycle` é refactorizado para
  receber `RunCoordinator` e usar o sink em vez de chamar
  directamente `TelegramScraperService.SearchAndTestM3UInTelegramAsync`.
- O novo `POST /api/run/start` chama `RunCoordinator.RunAsync`
  com um `body` que internamente invoca o mesmo
  `SearchAndTestM3UInTelegramAsync` (com os mesmos parâmetros
  activos do processo — país, `--max-streams`, `--history-hours`,
  etc.). Os defaults vêm do que foi configurado no arranque.

### 5.4 Trigger manual: `POST /api/run/start`

Contrato:

```http
POST /api/run/start
Authorization: Bearer <webToken>      # se --web-token
Content-Type: application/json

{
  "mode": "telegram" | "telegram-maintain",   # default: "telegram-maintain"
  "keyword": "portugal",                       # opcional
  "historyHours": 24,                          # opcional, default 24
  "maxStreams": 500                            # opcional, default 500
}
```

Respostas:

- `202 Accepted` — run iniciado. Body:
  ```json
  { "runId": "<guid>", "startedAtUtc": "...", "mode": "...", "message": "Run started." }
  ```
- `409 Conflict` — já existe run activo. Body:
  ```json
  {
    "error": "Já existe um ciclo em curso.",
    "currentRunId": "<guid>",
    "startedAtUtc": "...",
    "mode": "..."
  }
  ```
- `503 Service Unavailable` — pipeline Telegram não está
  configurada para correr (caso `--web` standalone sem
  credenciais Telegram ou sem `wtelegram.config` utilizável).
  Body:
  ```json
  { "error": "Pipeline Telegram não configurada neste processo." }
  ```
- `400 Bad Request` — payload inválido.
- `401 Unauthorized` — se `--web-token` estiver configurado e o
  token não for fornecido (mesmo gate do resto do Dashboard —
  `WebDashboardService.cs:128-137`).

Notas:

- O trigger é **fire-and-forget**: o cliente não fica à espera
  do fim do ciclo. O progresso observa-se via
  `GET /api/run/status`.
- Os parâmetros enviados no payload **sobrepõem-se** aos
  defaults activos do processo (não persistem — só afectam este
  run). Para persistir defaults, usa-se a configuração normal
  (`wtelegram.config` + `runtime-data/`).
- `mode: "telegram"` faz um único `SearchAndTestM3UInTelegramAsync`
  sem merge com a playlist anterior. Equivalente a `--telegram
  <keyword> --history-hours N --max-streams M`.
- `mode: "telegram-maintain"` faz o ciclo completo
  (discovery → validate → merge). Equivalente a
  `--telegram-maintain --loop-hours N` mas com `loop-hours=0`
  (corre uma vez e termina).

### 5.5 Estado: `GET /api/run/status`

Contrato:

```http
GET /api/run/status
```

Resposta (200):

```json
{
  "isRunning": true,
  "runId": "<guid>",
  "mode": "telegram",
  "status": "running",
  "startedAtUtc": "2026-09-11T10:30:00Z",
  "lastUpdatedAtUtc": "2026-09-11T10:31:24Z",
  "finishedAtUtc": null,
  "durationMs": 84234,
  "phase": "validate",
  "phaseIndex": 2,
  "phases": ["discovery","parse","validate","report"],
  "phaseStartedAtUtc": "2026-09-11T10:31:00Z",
  "counts": {
    "messagesAnalyzed": 412,
    "candidatesFound": 27,
    "playlistsDownloaded": 22,
    "playlistsInvalid": 1,
    "playlistsRejected": 16,
    "countryMatches": 6,
    "streamsExtracted": 184,
    "streamsAfterCountryFilter": 142,
    "streamsRejectedByCountry": 42,
    "streamsTested": 38,
    "streamsWorking": 27,
    "streamsFailed": 11
  },
  "estimate": {
    "estimatedRemainingMs": 210000,
    "estimatedTotalMs": 294234
  },
  "lastMessage": "validated 38/142 streams",
  "sanitized": true,
  "source": "live"   // "live" se do RunCoordinator; "stale" se do ficheiro
}
```

Quando não há run activo (idle):

```json
{
  "isRunning": false,
  "status": "idle",
  "lastRun": {
    "runId": "<guid>",
    "mode": "telegram-maintain",
    "startedAtUtc": "...",
    "finishedAtUtc": "...",
    "durationMs": 612400,
    "status": "completed",
    "phase": "report",
    "phaseIndex": 4,
    "phases": ["discovery","parse","validate","merge","report"]
    // counts resumidos (ver §5.6)
  }
}
```

Quando o snapshot em disco é mais antigo que 60s e
`RunCoordinator.IsRunning == false`, devolve
`{ "isRunning": false, "status": "stale", ... }` com
`source: "stale"` — protege contra estados zombie após
crash do processo.

Quando nunca correu: `{ "isRunning": false, "status": "idle" }`
sem `lastRun`.

Estimativas:

- `EstimatedTotalMs` = mediana móvel das últimas 5 durações de
  runs do mesmo `mode` (lidas de
  `ImportHistoryService.GetRecentAsync(...)` —
  `m3uCrawler/Services/ImportHistoryService.cs`).
- `EstimatedRemainingMs` =
  `EstimatedTotalMs - durationMs` (clamp ≥ 0).
- Se não houver histórico suficiente (< 3 runs),
  `estimate` é omitido.

### 5.6 Compatibilidade com `RunReport` e `telegram_run_report.json`

`RunReport` continua imutável quanto ao schema. Adições
opcionais (mantidas como nullable para retro-compatibilidade):

- `RunId` (string, nullable) — GUID do ciclo.
- `ProgressPath` (string, nullable) — caminho relativo do
  `RunProgressSnapshot` correspondente.

Nenhuma ferramenta existente (Dashboard, scripts, runbooks)
precisa de mudar: ambos os campos são opcionais e ignorados
quando ausentes.

`SaveRunReportAsync` (em `Program.cs`) é o único sítio que
precisa de saber o `RunId` — recebe-o via
`RunCoordinator.RunAsync(...)` que injecta um
`RunProgressSink` no pipeline, e o sink propaga o `RunId`
para o `RunReport` no fim.

### 5.7 `RunProgressSink` (interface injetável)

Nova interface em `m3uCrawler/Services/Automation/`:

```csharp
public interface IRunProgressSink
{
    void PhaseStarted(string phase);
    void PhaseFinished(string phase);
    void UpdateCounts(Action<RunProgressCounts> mutate);
    void Message(string sanitizedMessage);
    Task FlushAsync(CancellationToken ct);
    string RunId { get; }
}
```

Implementação `FileRunProgressSink` (default):

- Throttle de 250ms sobre escritas em disco.
- Atomic write via `*.tmp` + `File.Move(..., overwrite: true)`.
- Sanitização automática de qualquer string que possa conter
  URL via `CredentialSanitizer.SanitizeUrl`.

Implementação `InMemoryRunProgressSink` (testes):

- Captura transições e contadores numa lista, sem I/O.
- Útil para testes unitários do pipeline.

### 5.8 Instrumentação concreta no `TelegramScraperService`

`SearchAndTestM3UInTelegramAsync` (assinatura actual
preservada — novo parâmetro opcional no fim):

```csharp
public async Task<RunReport> SearchAndTestM3UInTelegramAsync(
    string keyword,
    int maxUrlsToTest = 500,
    int historyHours = 24,
    string countryCode = "pt",
    string? countriesDir = null,
    RunReport? report = null,
    IRunProgressSink? progress = null,         // NOVO (opcional)
    CancellationToken cancellationToken = default)  // NOVO (opcional)
```

Pontos de chamada:

- Início (linha ~131): `progress?.PhaseStarted("discovery")`.
- Após `SearchM3UInTelegramInternal` (~linha 135):
  `progress?.PhaseFinished("discovery")`,
  `progress?.UpdateCounts(c => { c.MessagesAnalyzed = …;
  c.CandidatesFound = …; })`,
  `progress?.PhaseStarted("parse")`.
- `for (ci = 0; ci < candidates.Count; ci++)`:
  dentro do loop, `progress?.UpdateCounts(...)` com
  `PlaylistsDownloaded`, `PlaylistsInvalid`, `PlaylistsRejected`,
  `CountryMatches`, `StreamsExtracted`,
  `StreamsAfterCountryFilter`, `StreamsRejectedByCountry`,
  `StreamsTested`, `StreamsWorking`, `StreamsFailed`.
- Após o `for`: `progress?.PhaseFinished("validate")`,
  `progress?.PhaseStarted("report")`,
  `progress?.Message("completed")`, `progress?.FlushAsync(...)`.
- `catch` excepções: `progress?.Message("failed: <sanitized>")`,
  flush, repassar.

Padrão idêntico em `RunTelegramMaintenanceCycle` (em
`Program.cs`), com a fase adicional `merge`.

Compatibilidade: os parâmetros novos são opcionais com
default `null`/`default`, pelo que todas as chamadas internas
existentes (testes, dashboard, CLI) continuam a funcionar
exactamente como antes. **Zero alterações de comportamento
observável** sem o sink ser injectado.

### 5.9 Scheduled Jobs (PHASE 12) — integração opcional

`IScheduledAction` existente
(`IMPLEMENTATION_ROADMAP §32.10`) já suporta acções
programáveis. A wave pode expor uma acção concreta
`runTelegramCycle` (registada em `Program.cs` no boot) que
internamente delega para `RunCoordinator.RunAsync(...)`. Isto
fecha o gap pendente da PHASE 12 ("Acções concretas e arranque
em produção") para pelo menos uma acção.

Não é **obrigatório** para esta wave. Decisão: incluir se o
esforço for marginal (~10 linhas). Caso contrário, deixar como
follow-up da PHASE 12 já existente. Recomendação: incluir,
dado que dá imediatamente um caminho "agendar um ciclo agora"
a partir do Dashboard tab `Scheduled Jobs`.

---

## 6. Endpoints e ficheiros

### 6.1 Endpoints novos

| Método | Path                  | Auth         | Descrição |
|--------|-----------------------|--------------|-----------|
| GET    | `/api/run/status`     | `--web-token`| Estado corrente (idle / running / completed / failed / stale) + snapshot |
| POST   | `/api/run/start`      | `--web-token`| Dispara ciclo manual (async, 202 + runId) |

### 6.2 Endpoints inalterados

- `GET /api/run-report`, `GET /api/run-report/summary`,
  `GET /api/discovered-playlists`, `GET /api/history`,
  `GET /api/execution/{idx}` — mantêm-se como fontes de
  diagnóstico pós-run. Continuam a ler
  `output/telegram_run_report.json` + `import_history.json`.
- Toda a API de catálogo (`/api/catalog/*`), países,
  playlist, Dispatcharr, scheduled jobs, validation policy —
  inalterada.

### 6.3 Ficheiros novos

```
m3uCrawler/Models/RunProgressSnapshot.cs                # NOVO (DTO do snapshot)
m3uCrawler/Services/Automation/IRunProgressSink.cs      # NOVO (interface)
m3uCrawler/Services/Automation/FileRunProgressSink.cs   # NOVO (default)
m3uCrawler/Services/Automation/InMemoryRunProgressSink.cs # NOVO (testes)
m3uCrawler/Services/Automation/RunCoordinator.cs        # NOVO (lock + dispatch)
m3uCrawler/Services/Automation/RunAlreadyInProgressException.cs # NOVO
m3uCrawler.Tests/RunProgressSnapshotTests.cs            # NOVO
m3uCrawler.Tests/RunCoordinatorTests.cs                # NOVO
m3uCrawler.Tests/RunDashboardApiTests.cs               # NOVO (HTTP via loopback)
docs/architecture/run-observability-and-manual-trigger.md # NOVO (este doc)
```

### 6.4 Detecção de stale

`FileRunProgressSink` regista `LastUpdatedAtUtc` em cada
escrita. `GET /api/run/status` lê o ficheiro e compara:

- Se `RunCoordinator.IsRunning == true` e o ficheiro existe →
  `status: "running"`, `source: "live"`.
- Se `RunCoordinator.IsRunning == true` e o ficheiro **não**
  existe → erro de instrumentação: log + responder
  `status: "running"` sem contadores (`source: "live-no-sink"`).
- Se `RunCoordinator.IsRunning == false` e o ficheiro existe
  com `LastUpdatedAtUtc` há menos de 60s → tratar como
  provavelmente concluído mas ainda não limpo: `status: "stale"`.
- Se `RunCoordinator.IsRunning == false` e o ficheiro é
  antigo (> 60s) → `status: "idle"`, ler `lastRun` do ficheiro.
- Se nunca houve run → `status: "idle"`, sem `lastRun`.

O ficheiro é apagado **no fim** do run (`Status = "completed"`
ou `"failed"`) pelo próprio sink. Runs abortados (Ctrl-C) deixam
o ficheiro; a próxima execução apaga-o no início do seu
próprio ciclo (já descrito em §5.1).

---

## 7. UI no Dashboard

### 7.1 Bloco novo no Overview

Nova secção no topo da página Overview (já existente):

```text
┌─ Run status ──────────────────────────────────────────┐
│ ● Running   ciclo #a3f7…  há 4m 12s                   │
│ Fase: validate (3/5)                                  │
│ Streams: 38/142 testados · 27 working · 11 failed     │
│ ETA: ~3m 30s                                          │
│                                          [Stop run]   │ ← placeholder; cancel é out-of-scope (§4.2)
└────────────────────────────────────────────────────────┘

Quando idle:

┌─ Run status ──────────────────────────────────────────┐
│ ○ Idle   último ciclo há 12h (completed, 10m 12s)     │
│                                          [Run now ▶]  │
└────────────────────────────────────────────────────────┘
```

Comportamento:

- Polling a `GET /api/run/status` cada 2s enquanto
  `isRunning == true`; caso contrário, cada 10s.
- A "ETA" usa `estimate.estimatedRemainingMs` se presente;
  sem isso, mostra apenas o tempo decorrido.
- "Run now" abre um pequeno modal com:
  - Dropdown `Mode`: `Telegram (single cycle)` / `Telegram (maintenance cycle)`.
  - Inputs opcionais: `Keyword`, `History hours`, `Max streams`.
  - Botão "Start" → `POST /api/run/start`.
  - Após 202: troca o botão para "Run already started",
    actualiza o badge para running, e passa a polling 2s.
- Se a resposta for 409, o modal mostra
  "Já existe um ciclo em curso (run #…, começou há Xm)" e
  fecha automaticamente ao fim de 3s.

### 7.2 Tab "Runs" (secção existente)

Refinamento mínimo: a tabela de execuções ganha uma coluna
`Run ID` (curto: primeiros 8 chars do GUID) que é clicável
para um modal com o snapshot completo do
`RunProgressSnapshot` desse run (lido do histórico).

### 7.3 Comportamento quando `--web` standalone

Se o Dashboard correr **sem** pipeline Telegram configurada
(ex.: só `--web` sem credenciais Telegram, ou container com
`wtelegram.config` ausente), `GET /api/run/status` continua a
funcionar (devolve `idle` ou `stale`). `POST /api/run/start`
responde **503** com mensagem clara. O botão "Run now" mostra
tooltip "Pipeline Telegram não configurada neste processo".

---

## 8. Compatibilidade e migrações

### 8.1 Schema

- **Nenhuma migração EF Core** é necessária — o snapshot é em
  ficheiro JSON, não em SQLite. Segue o padrão já usado em
  `output/telegram_run_report.json` e `runtime-data/stream_validation_policy.json`.
- **Nenhuma alteração** a `RunReport` é obrigatória para
  retro-compatibilidade (campos opcionais). Se forem
  adicionados (`RunId`, `ProgressPath`), são `string?` e
  ignorados por todos os consumidores quando ausentes.

### 8.2 Comportamento

- Modo CLI puro (sem Dashboard) — comportamento **idêntico** ao
  actual. O `IRunProgressSink` é opcional; sem ele, o pipeline
  funciona exactamente como hoje.
- Dashboard standalone (sem Telegram) — `GET /api/run/status`
  responde `idle`; `POST /api/run/start` responde 503.
- Dashboard + Telegram loop manual — funciona, mas a barra de
  progresso aparece imediatamente e refresca a cada 2s.
- Dashboard + Telegram ciclo existente + Scheduled Job — os
  dois coexistem porque o `RunCoordinator` é o ponto único de
  serialização.

### 8.3 Rollback

- Remover os 3 endpoints, o ficheiro
  `output/telegram_run_progress.json` (se existir), e os
  ficheiros novos. O resto do sistema volta ao estado anterior.
- O `RunCoordinator` é opcional; os call-sites passam a
  chamar `TelegramScraperService.SearchAndTestM3UInTelegramAsync`
  directamente como hoje.

---

## 9. Segurança

Reutiliza integralmente o modelo de segurança do Dashboard
(`AGENTS.md §2`, "Modelo de segurança do dashboard"):

- Se `--web-token` está configurado, **todos** os endpoints
  do dashboard (incluindo `/api/run/status` e `/api/run/start`)
  exigem o mesmo token via `Authorization: Bearer` ou `?token=`.
  O gate está em `WebDashboardService.IsRequestAuthorized(...)`
  (`WebDashboardService.cs:128-137`).
- `POST /api/run/start` **não** devolve credenciais Telegram,
  URLs, nem tokens. Devolve apenas `runId` + `startedAtUtc` +
  `mode` + mensagem genérica.
- `GET /api/run/status` **não** devolve URLs, títulos de canais,
  nem detalhes de stream. Só contadores + fase corrente.
- O ficheiro `output/telegram_run_progress.json` é gerado em
  `output/` (que em produção é `/opt/m3ucrawler/runtime-data/output/`,
  bind mount — `DEPLOYMENT.md`). Permissões consistentes com
  os outros artefactos em `output/`.

Risco residual:

- O trigger manual pode iniciar um ciclo que faz chamadas de
  rede para Telegram e para os hosts Xtream descobertos. Em
  ambientes onde o operador do Dashboard não é de confiança
  (rede exposta sem `--web-token`), isto é um problema de
  segurança existente — não introduzido por esta wave.
  Alinhamento: `AGENTS.md §2` recomenda
  `--web-token` em deployments não locais.

---

## 10. Plano de testes

### 10.1 Unitários

`RunProgressSnapshotTests.cs`:

- Snapshot serializa/deserializa com `System.Text.Json`
  camelCase preservando `Counts` nested.
- `Sanitized = true` é preservado na round-trip.
- Throttle: 100 `UpdateCounts` em 100ms → no máximo 4 escritas
  em disco.
- Atomic write: falha de I/O no rename não corrompe ficheiro
  existente.

`RunCoordinatorTests.cs`:

- Duas chamadas paralelas a `RunAsync`: a segunda lança
  `RunAlreadyInProgressException`.
- Cancelamento do `CancellationToken` interrompe o `body` em
  ≤ 1s.
- Após `body` terminar com sucesso, `IsRunning` volta a
  `false` e o `RunId` é limpo.
- Após excepção do `body`, `IsRunning` volta a `false` e o
  snapshot é gravado com `status: "failed"`.

`InMemoryRunProgressSinkTests.cs` (ou parte de
`RunProgressSnapshotTests.cs`):

- `PhaseStarted` / `PhaseFinished` registam transições na
  ordem correcta.
- `Message` aplica sanitização a URLs embutidas.

### 10.2 HTTP via loopback (pattern PHASE 2)

`RunDashboardApiTests.cs` (segue o padrão de
`WebDashboardHttpApiTests.cs` — `IMPLEMENTATION_ROADMAP §32.4`):

- `GET /api/run/status` devolve 401 sem token quando
  `--web-token` configurado.
- `POST /api/run/start` devolve 401 sem token.
- `GET /api/run/status` em idle devolve `{ isRunning: false,
  status: "idle" }`.
- `POST /api/run/start` dispara ciclo fake via
  `RunCoordinator` com `InMemoryRunProgressSink`; responde
  202 + `runId`.
- Imediatamente a seguir, `POST /api/run/start` devolve 409
  com `currentRunId` igual ao anterior.
- Após o ciclo fake terminar, `GET /api/run/status` devolve
  `{ isRunning: false, status: "idle", lastRun: { runId, ... } }`.

### 10.3 Cobertura de regressão

Não há regressão esperada nos testes existentes (PHASE 0–12,
1190+ testes). O `TelegramScraperService.SearchAndTestM3UInTelegramAsync`
ganha dois parâmetros opcionais no fim; todas as chamadas
existentes continuam a passar com defaults `null`/`default`.

---

## 11. Critérios de aceitação (Definition of Done)

Alinhado com `IMPLEMENTATION_ROADMAP §33`:

1. **Código** — `RunProgressSnapshot`, `IRunProgressSink`,
   `FileRunProgressSink`, `InMemoryRunProgressSink`,
   `RunCoordinator`, `RunAlreadyInProgressException`,
   instrumentação em `TelegramScraperService` e em
   `RunTelegramMaintenanceCycle`, 2 endpoints novos em
   `WebDashboardService`.
2. **Persistência** — escrita atómica de
   `output/telegram_run_progress.json` com throttle de 250ms.
3. **API** — `GET /api/run/status` + `POST /api/run/start`,
   ambos gated por `--web-token` quando aplicável, ambos com
   sanitização.
4. **Dashboard** — bloco "Run status" no Overview, modal
   "Run now", coluna `Run ID` no tab Runs.
5. **Testes** — `RunProgressSnapshotTests`,
   `RunCoordinatorTests`, `RunDashboardApiTests`.
6. **Build** — `dotnet build m3uCrawler.sln --configuration Release --no-restore`
   → 0 warnings, 0 errors.
7. **Testes** — `dotnet test m3uCrawler.Tests/m3uCrawler.Tests.csproj --configuration Release --no-build --nologo`
   → todos a passar (número concreto é estado volátil, datado
   no momento da entrega).
8. **Documentação afectada actualizada:**
   - `m3uCrawler/README.md` — nova entrada em "Dashboard"
     para `/api/run/status` + `/api/run/start`; nova entrada
     em "Argumentos da linha de comandos" se o trigger manual
     ficar opt-in por flag (decisão em §12.2).
   - `docs/IMPLEMENTATION_ROADMAP.md` — adicionar a entrega
     em `§32.x` (próximo número livre), actualizar tabela
     `§32.1` (snapshot por fase) e tabela "Apêndice".
   - `OPERATIONS.md` — referência ao ficheiro
     `output/telegram_run_progress.json` como artefacto de
     diagnóstico recente.
   - `CHANGELOG.md` — entrada em `[Unreleased]`.
9. **Compatibilidade** — sem alteração de schema, sem
   alteração de comportamento CLI, sem alteração de
   `RunReport`.
10. **Revisão** — `/review` opcional antes do commit.

---

## 12. Pontos de decisão em aberto

Os pontos seguintes são **decisões** a tomar **antes** da
implementação (não são ambiguidades do plano; são opções de
design a fixar):

### 12.1 `RunId` em `RunReport`

**Opção A** — Adicionar `RunId` (string?) ao `RunReport`.
Pró: permite correlação com logs futuros, snapshots e
histórico. Contra: mudança ao schema (mesmo que aditiva).

**Opção B** — Manter `RunId` apenas no
`RunProgressSnapshot` e em `ImportHistoryEntry`. Pró: zero
alterações a `RunReport`. Contra: precisa de correlação por
`startedAtUtc` aproximado, o que é frágil se houver runs
consecutivos.

**Recomendação:** Opção A. Justificação: a mudança é
puramente aditiva (campo nullable) e o valor de correlação é
alto. Nenhum consumidor existente precisa de mudar (campo
opcional). Caso o utilizador prefira zero-ruptura, fica
Opção B como fallback.

### 12.2 Trigger manual opt-in por flag CLI?

**Opção A** — `POST /api/run/start` activo por defeito se o
Dashboard estiver a correr. Pró: zero configuração adicional.
Contra: dá ao Dashboard um botão "Run now" sempre que
alguém com acesso ao Dashboard queira; em deployments com
Dashboard exposto em rede sem `--web-token`, isto pode ser
indesejado.

**Opção B** — Adicionar flag `--web-allow-trigger` (default
`false`). Pró: opt-in explícito. Contra: mais uma flag a
documentar.

**Recomendação:** Opção B. Justificação: alinha com o
princípio "não expor superfícies sem opt-in explícito". O
trigger manual é uma superfície nova; merece flag dedicada.
Combinada com `--web-token` (recomendado em deployments não
locais), o resultado é seguro.

### 12.3 Cancelamento de run em curso

**Opção A** — Incluir `POST /api/run/cancel` nesta wave.
Pró: UX completa. Contra: complexidade adicional (token
partilhado, race conditions, estado consistente pós-cancel).

**Opção B** — Deferir para wave posterior.
Pró: plano focado. Contra: "Stop run" tem de ser removido da
UI (§7.1 mostra-o como placeholder, mas se não houver
endpoint tem de ser escondido).

**Recomendação:** Opção B. Justificação: o foco da wave é
**observabilidade** + **trigger**. Cancelamento é um
problema separado que merece um documento próprio.

### 12.4 ETA / estimativa

**Opção A** — Implementar com mediana móvel das últimas 5
runs (descrita em §5.5).
**Opção B** — Sem ETA; mostrar apenas tempo decorrido.

**Recomendação:** Opção A. É barato (10 linhas em
`RunCoordinator` + leitura de `ImportHistoryService`) e o valor
para o operador é alto. Implementação trivial.

### 12.5 Acção `runTelegramCycle` em `IScheduledAction`

**Opção A** — Incluir nesta wave (§5.9).
**Opção B** — Deferir para fecho da PHASE 12.

**Recomendação:** Opção A. Fecha um gap real da PHASE 12
com pouco esforço adicional.

---

## 13. Riscos e mitigações

| Risco                                                                                       | Mitigação                                                                                                |
|---------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------|
| Throttle de 250ms escreve snapshots stale quando o run termina abruptamente (Ctrl-C)         | Detecção de stale em §6.4 (60s). Sink apaga ficheiro em `Status=completed/failed`.                       |
| RunCoordinator singleton pode ser contornado por múltiplas instâncias do processo            | Lock é por processo, não distribuído. Para deployments com múltiplas instâncias, adicionar flag `--web-no-trigger` para uma das réplicas. **Fora do âmbito** desta wave. |
| `POST /api/run/start` pode ser abusado para DoS (runs repetidos)                              | Gate por `--web-token` (§9). Rate limiting fica para wave futura dedicada.                              |
| Influxo de escritas a disco se muitas `UpdateCounts` por segundo                              | Throttle de 250ms (§5.1). Worst-case: ~4 writes/s, negligível em SSD moderno.                            |
| Estimativas enganadoras em playlists pequenas / grandes                                      | ETA só é apresentada se houver ≥3 runs anteriores (§5.5). Caso contrário, omitido.                       |
| UI de progresso pode "saltar" se fase termina entre polls                                     | Polling 2s; o cliente cruza `phaseIndex` + `counts` para interpolar. Documentado em §7.1.                |
| Schema de `RunReport` muda e quebra consumidores antigos (se §12.1 for Opção A)              | Campos nullable; ausência = comportamento actual. Sem migrações necessárias (JSON, não SQLite).          |
| Cancelamento via Ctrl-C deixa snapshot stale                                                 | Próximo run apaga-o no início (§5.1, §6.4).                                                             |

---

## 14. Resumo executivo

| Item                                | Estado                        |
|-------------------------------------|-------------------------------|
| Plano                               | ✅ este documento             |
| Implementação                       | `[pendente]`                  |
| Build                               | `[pendente]`                  |
| Testes                              | `[pendente]`                  |
| Docs afectadas actualizadas         | `[pendente]`                  |
| Changelog actualizado               | `[pendente]`                  |
| Commit                              | `[pendente — apenas se pedido]` |

Wave estimada: **média**. Tocar em:
`Models/RunProgressSnapshot.cs` (NOVO),
`Services/Automation/{IRunProgressSink,FileRunProgressSink,InMemoryRunProgressSink,RunCoordinator,RunAlreadyInProgressException}.cs` (NOVO),
`Services/TelegramScraperService.cs` (instrumentação opcional, sem mudança de comportamento observável),
`Program.cs` (`RunTelegramMaintenanceCycle` instrumentado + registo do `RunCoordinator` no DI + registo da acção `runTelegramCycle` em `IScheduledAction`),
`Services/WebDashboardService.cs` (2 endpoints novos + UI mínima).

Não tocar: `Models/RunReport.cs` (campos opcionais apenas se §12.1 = A), `Services/CredentialSanitizer.cs`,
`Services/M3uParserService.cs`, `Services/PlaylistManagerService.cs`,
qualquer ficheiro de `Services/Catalog/*` (catálogo, matching,
composer, ordering, source priority — todas as fases já
concluídas, nenhuma regressão necessária), `Dockerfile`,
`docker-compose.yml`, workflows em `.github/workflows/*`,
`Directory.Build.props`/`Directory.Build.targets`.

---

## 15. Cross-references

- `AGENTS.md §2` — invariantes (manutenção preserva playlist,
  dashboard independente do Telegram, sanitização, ownership).
- `m3uCrawler/README.md` §"Dashboard" e §"Comportamento funcional".
- `IMPLEMENTATION_ROADMAP.md §11` (PHASE 2), `§22` (PHASE 11),
  `§30` (PHASE 12), `§32.4` (HTTP tests pattern),
  `§32.10` (Scheduled Jobs), `§33` (DoD), `§34` (regra fundamental).
- `docs/architecture/channel-catalog-and-ownership.md` — para
  evitar conflitar com regras de ownership de streams.
- `DEPLOYMENT.md` — bind mount em `/opt/m3ucrawler/runtime-data/`;
  novo ficheiro cai em `/opt/m3ucrawler/runtime-data/output/telegram_run_progress.json`.
- `OPERATIONS.md` — actualizado com referência ao novo
  artefacto de diagnóstico.

---

## 16. Scheduled Start Time (hora fixa da primeira execução + recorrência)

### 16.1 Contexto específico

O utilizador pretende que:

```text
--telegram-maintain --loop-hours 24 --start-at 03:00
```

signifique:

- O **primeiro** run acontece às **03:00 UTC** do dia mais próximo
  em que essa hora ainda não passou (ou no próprio dia, se for
  configurado para arrancar antecipadamente — ver §16.5).
- Os runs seguintes acontecem a cada `--loop-hours` **a contar
  da primeira execução**, alinhados à hora configurada, **não**
  a contar do fim do run anterior.

Isto aplica-se a:

- **CLI** (`--telegram-maintain --loop-hours N --start-at HH:MM`).
- **Dashboard → Scheduled Jobs** (a job já tem `CronExpression`;
  esta secção clarifica como o campo `CronExpression` resolve
  este caso e o que a UI deve mostrar/criar).

A PHASE 12 (ver `IMPLEMENTATION_ROADMAP §32.10`) já introduziu
`CronExpression` (5 campos, UTC) e `ScheduledJobRunner`. O
formato cron padrão é a ferramenta canónica para "hora fixa +
recorrência":

```text
"03:00 UTC todos os dias"            → 0 3 * * *
"a cada 6h a começar às 03:00 UTC"    → 0 */6 * * *
"de 24 em 24h a partir das 03:00 UTC"→ 0 3 * * *     (recurso a horas inteiras)
```

Por outras palavras, o problema conceptual que o utilizador
descreve (recorrência + hora da primeira execução) **já tem**
resposta natural no cron: o campo `hours` fixa a hora; o
`*/N` ou lista específica fixa a recorrência. Esta secção
define como isso se manifesta (a) na CLI, (b) no Dashboard, e
(c) como se relaciona com `--loop-hours` actual.

### 16.2 Princípios para o CLI

- **Backward compatibility total**: o novo parâmetro é
  opcional. Sem ele, o comportamento é exactamente o actual
  (`Task.Delay(TimeSpan.FromHours(loopHours))` a contar do fim
  do run anterior).
- **Timezone**: UTC. Não confundir com o timezone do
  container/host — `CronExpression.NextOccurrence` devolve UTC
  e `m3uCrawler/Services/Automation/CronExpression.cs:18`
  declara "Timezone: UTC" como limitação conhecida. Esta wave
  mantém a invariante.
- **Formato CLI**: `--start-at HH:MM` (UTC, 24h, minutos 00–59).
  Validação eager no parse (ver §16.6).
- **Independência**: `--start-at` sem `--loop-hours` é um erro
  (não faz sentido "hora de início fixa" sem recorrência).
  `--loop-hours` sem `--start-at` mantém o comportamento
  actual (relativo ao fim do run anterior).
- **Validação cruzada**: se `--loop-hours` for 1 ou superior,
  `--start-at` deve ser múltiplo natural do intervalo. Ex.:
  `--loop-hours 6 --start-at 03:15` — o primeiro run é às
  03:15; o seguinte às 09:15; depois 15:15; depois 21:15;
  depois 03:15 do dia seguinte. Não se rejeita (é válido);
  apenas se documenta este comportamento.

### 16.3 Comportamento concreto na CLI

Reescrita mínima do loop actual
(`m3uCrawler/Program.cs:206-307`):

```text
bool useAbsoluteSchedule = loopHours > 0
                          && startAtUtc.HasValue
                          && maintenanceMode;

if (useAbsoluteSchedule)
{
    var firstRunAt = ComputeFirstRunAtUtc(startAtUtc.Value, loopHours);
    var initialDelay = firstRunAt - DateTime.UtcNow;
    if (initialDelay > TimeSpan.Zero)
    {
        Console.WriteLine($"⏰ Próxima execução agendada para {firstRunAt:o}");
        await Task.Delay(initialDelay);
    }
    else
    {
        Console.WriteLine($"⏰ Primeira execução imediata (já passou {firstRunAt:o})");
    }

    while (!cts.Token.IsCancellationRequested)
    {
        if (maintenanceMode)
        {
            await RunTelegramMaintenanceCycle(...);
        }

        var nextAt = ComputeNextRunAtUtc(DateTime.UtcNow, firstRunAt, loopHours);
        Console.WriteLine($"⏰ Próxima execução agendada para {nextAt:o}");
        await Task.Delay(nextAt - DateTime.UtcNow, cts.Token);
    }
}
else
{
    // Comportamento actual — exactamente como hoje.
    do
    {
        if (maintenanceMode) { await RunTelegramMaintenanceCycle(...); }
        if (loopHours > 0) { await Task.Delay(TimeSpan.FromHours(loopHours)); }
    } while (loopHours > 0);
}
```

`ComputeFirstRunAtUtc(StartAtUtc start, int intervalHours)`:

- Recebe `start.Hour` e `start.Minute` em UTC.
- Devolve `DateTime.UtcNow` arredondado para o **próximo**
  múltiplo do `intervalHours` (em horas) cuja hora/minuto
  coincidam com `start.Hour`/`start.Minute`.

  Algoritmo:

  ```text
  candidates = []
  today       at start.Hour:start.Minute
  tomorrow    at start.Hour:start.Minute
  today       at start.Hour+intervalHours:start.Minute
  today       at start.Hour+2*intervalHours:start.Minute
  ... até ao próximo múltiplo > now
  return min(candidates > now)
  ```

- Para `loopHours = 24` e `startAt = 03:00`:
  se `now = 14:37 do dia X`, `firstRunAt = 03:00 do dia X+1`
  (14h37 de espera).
  se `now = 01:00 do dia X`, `firstRunAt = 03:00 do dia X`
  (2h de espera).
- Para `loopHours = 6` e `startAt = 03:00`:
  se `now = 14:37`, candidatos são 15:00 (hoje), 21:00
  (hoje), 03:00 (amanhã); `firstRunAt = 15:00` (~22 min).

`ComputeNextRunAtUtc(DateTime lastRunAt, DateTime firstRunAt,
int intervalHours)`:

- Devolve `firstRunAt + k * intervalHours` para o menor `k ≥ 1`
  tal que `firstRunAt + k * intervalHours > lastRunAt`.

  Exemplo: `firstRunAt = 03:00`, `lastRunAt = 03:14`
  (duração 14 min), `intervalHours = 24` → próximo = `03:00` do
  dia seguinte (não `03:14` do dia seguinte — alinhado à hora
  configurada, não ao fim do run anterior).

### 16.4 `--loop-hours` vs agendamento absoluto

Quando `--start-at` é fornecido, a semântica de `--loop-hours`
muda ligeiramente:

| Parâmetros                          | Semântica actual                                           | Semântica com `--start-at`                       |
|-------------------------------------|------------------------------------------------------------|--------------------------------------------------|
| `--loop-hours N`                    | Próximo run é `lastFinish + N` horas                       | Próximo run é `firstRunAt + k*N` para o menor `k≥1 > lastFinish` |
| `--loop-hours N --start-at HH:MM`   | (não aplicável)                                            | Próximo run é o próximo múltiplo de N a partir de `HH:MM` UTC |

Invariantes:

- **Duração do run não afecta o alinhamento** (com `--start-at`).
  O próximo tick é sempre à hora alinhada, mesmo que o run
  anterior tenha demorado muito.
- **Sobreposição**: se um run demorar mais do que
  `--loop-hours`, há **dois** runs no mesmo intervalo. Esta
  wave **não** resolve a sobreposição: continua a aplicar-se
  a invariante do `RunCoordinator` (lock por processo, sem
  runs paralelas — §5.3 do plano principal). O segundo run é
  descartado pelo lock (mecanismo já existente). O utilizador
  vê um "lock-acquired-during-prev-run" no relatório
  (`RunAlreadyInProgressException`); isto é aceitável porque o
  `RunCoordinator` é uma adição da wave principal — quando os
  dois planos forem fundidos, esta proteção já existirá.
- **Cancelamento**: Ctrl-C interrompe o `Task.Delay` e o run
  actual. Próximo arranque volta a calcular `firstRunAt` a
  partir do `now`.

### 16.5 Edge case: `startAt` no passado

Comportamento definido (sem ambiguidade):

- Se `--start-at HH:MM` é uma hora **anterior** a `now`, o
  primeiro run acontece no **próximo múltiplo** de
  `--loop-hours` cuja hora seja ≥ `now`. Documentado na
  mensagem inicial do ciclo.
- Se `--start-at HH:MM` é uma hora **posterior** a `now` no
  mesmo dia, o primeiro run é hoje a essa hora.
- Se `--start-at HH:MM` coincide aproximadamente com `now`
  (diferença inferior a 60 segundos), corre imediatamente.

Isto evita duas ambiguidades:
- "Atrasar 24h porque já passou a hora hoje" — não desejado
  para `loopHours = 24` (o operador quer o run o mais cedo
  possível dentro do alinhamento).
- "Correr imediatamente porque passou hoje" — também não
  desejado se ele acabou de configurar.

### 16.6 Validação

- `--start-at` aceita `HH:MM` (formato 24h, 5 caracteres, com
  `:` obrigatório). Aceita também `H:MM` (1 dígito na hora).
  Validação:
  - `0 ≤ hour ≤ 23`
  - `0 ≤ minute ≤ 59`
  - Padrão regex: `^([0-9]|1[0-9]|2[0-3]):[0-5][0-9]$`.
- Combinações inválidas:
  - `--start-at` sem `--loop-hours` → erro fatal com mensagem
    `--start-at requer --loop-hours`.
  - `--start-at` sem `--telegram-maintain` → erro fatal com
    mensagem `--start-at só se aplica a --telegram-maintain`
    (alinhamento absoluto só faz sentido no ciclo de
    manutenção; os outros modos são runs one-shot).
  - `--loop-hours 0` com `--start-at` → erro fatal com mensagem
    `--loop-hours deve ser > 0 quando combinado com --start-at`.

Mensagens de erro no mesmo estilo do `--loop-hours` actual
(`m3uCrawler/Program.cs:618`).

### 16.7 Integração com o `ScheduledJobRunner` (PHASE 12)

A PHASE 12 já tem o `ScheduledJobRunner` com `CronExpression`
e a UI tab `Scheduled Jobs`. O caso de uso que o utilizador
descreve **é exactamente o que o cron já resolve**:

```text
Job: "Telegram maintain (24h, alinhado às 03:00 UTC)"
CronExpression: "0 3 * * *"
ActionName: "runTelegramMaintainCycle"
IsEnabled: true
```

O `CronExpression.NextOccurrence(now)` calcula o próximo
alinhamento automaticamente — incluindo a primeira execução
após o arranque do processo.

**Mudança recomendada na wave principal (§5.9 do plano de
observabilidade)**: ao registar a acção concreta
`runTelegramMaintainCycle` em `IScheduledAction`, criar
também um `ScheduledJobEntity` por defeito na primeira
inicialização se `--telegram-maintain --loop-hours N
--start-at HH:MM` for passado na CLI. Migração suave:
- `Program.cs` arranca o `ScheduledJobRunner` no boot.
- Se houver jobs na BD, são usados.
- Senão, e se `--loop-hours N --start-at HH:MM` foi passado,
  o programa regista uma job por defeito com
  `CronExpression = "0 <HH> */N * * *"` (N ∈ {1..23},
  ajustar para o equivalente "a cada N horas a partir de HH").

  Implementação:
  - Se `N` divide 24 (1, 2, 3, 4, 6, 8, 12, 24):
    `CronExpression = "0 <HH> */N * * *"` →
    exemplos: `0 3 */6 * * *` (03, 09, 15, 21 UTC).
  - Se `N` não divide 24 (5, 7, 9, etc.): cria uma job com
    um cron sintético de 5 campos usando o motor existente.
    Para já, a abordagem recomendada é converter o pedido
    para um agendamento "a cada N horas" via
    `ScheduledMaintenanceIntervalAction` que internamente
    faz `Task.Delay` alinhado à primeira execução.
    **Decisão**: se `N ∉ {1,2,3,4,6,8,12,24}`, emitir um
    warning e cair para o comportamento CLI actual
    (`Task.Delay(TimeSpan.FromHours(N))` relativo ao fim).
    Justificação: o cron de 5 campos não cobre todos os
    divisores; uma wave dedicada a "cron estendido" ou um
    motor híbrido pode tratar isso depois.

Isto significa que o utilizador que hoje usa
`--loop-hours 24` tem **duas opções** após esta wave:

1. **Continuar a usar a CLI** com `--loop-hours 24
   --start-at 03:00` (mudança aditiva, retro-compatível).
2. **Migrar para a job persistida** (PHASE 12) com
   `CronExpression = "0 3 * * *"` e apagar a flag CLI.

Recomenda-se (a) como entrada e (b) como follow-up. A
documentação nova em `m3uCrawler/README.md` deve cobrir
ambas.

### 16.8 UI no Dashboard (tab Scheduled Jobs)

A tab `Scheduled Jobs` já tem formulário para criar/editar
jobs. Alterações mínimas:

- **Adicionar campo `StartAtUtc HH:MM`** ao formulário de
  criação/edição de uma job `runTelegramMaintainCycle`
  (helper específico — não se aplica a todas as acções).
- O formulário converte `StartAtUtc HH:MM` para a
  `CronExpression` equivalente (§16.7) ao persistir, e
  mostra a expressão cron resultante em modo read-only.
  Exemplo:
  - Input: `StartAtUtc = 03:00`, `LoopHours = 24`.
  - Persistido: `CronExpression = "0 3 * * *"`.
  - Mostra também `EffectiveCronExpression` ao utilizador
    para clareza.
- Validação idêntica à CLI: HH:MM em UTC, 0 ≤ HH ≤ 23,
  0 ≤ MM ≤ 59, `LoopHours > 0`, `LoopHours` ∈
  {1,2,3,4,6,8,12,24} ou warning.

Quando o utilizador escolhe uma `ActionName` diferente de
`runTelegramMaintainCycle`, o campo `StartAtUtc` fica oculto
(greyed out) porque o mapeamento é específico.

A `CronExpression` directa continua editável para power
users (campo opcional "advanced"): mostra um input texto
validado pelo `CronExpression.Parse`.

### 16.9 Integração com o plano de observabilidade

- O `GET /api/run/status` (§5.5) já lê `LastRunAtUtc` e
  `NextRunAtUtc` quando a fonte é o `RunCoordinator`.
  Adicionar campo opcional `NextScheduledRunAtUtc` ao
  payload, sourced da `ScheduledJobEntity` correspondente
  (`ActionName = "runTelegramMaintainCycle"`). Permite à
  UI mostrar "próximo run em 4h 23m" sem precisar de saber
  se está em modo CLI ou em job persistida.
- O `POST /api/run/start` (§5.4) **não é afectado** — é
  um trigger manual independente do agendamento.

### 16.10 Compatibilidade e migração

- **CLI**: `--start-at` é flag nova, opt-in. Sem ela,
  comportamento actual.
- **Dashboard**: nova coluna / campo no tab `Scheduled Jobs`,
  opt-in (só aparece para `ActionName =
  "runTelegramMaintainCycle"`).
- **Schema**: `ScheduledJobEntity` mantém-se inalterado. A
  conversão `StartAtUtc + LoopHours → CronExpression` é
  feita na camada de UI/API, não na BD. Jobs antigas (PHASE
  12) com `CronExpression` directa continuam a funcionar
  sem migração.
- **Jobs existentes**: zero impacto. Continuam a correr como
  antes.

### 16.11 Critérios de aceitação específicos

Alinhado com `IMPLEMENTATION_ROADMAP §33`:

1. **Código** — parser `--start-at HH:MM`, funções
   `ComputeFirstRunAtUtc` e `ComputeNextRunAtUtc` (puras,
   testáveis), branches no loop principal de
   `RunTelegramMaintenanceCycle` (em `Program.cs`).
2. **UI** — campo `StartAtUtc` no formulário de
   `ScheduledJob` quando `ActionName =
   "runTelegramMaintainCycle"`; conversão visível para
   `EffectiveCronExpression`; validação HH:MM UTC.
3. **API** — payload `POST /api/catalog/scheduled-jobs`
   aceita `startAtUtc?: string` (HH:MM); persiste a
   `CronExpression` derivada. Resposta inclui
   `effectiveCronExpression`.
4. **Testes**:
   - `ComputeFirstRunAtUtcTests` — casos típicos,
     edge cases (now = 00:00, now = 23:59, now = HH:MM
     exacto, loopHours = 1, 6, 24, intervalo que não divide
     24).
   - `ComputeNextRunAtUtcTests` — alinhamento com duração
     variável do run anterior; múltiplos ciclos; duração
     > intervalo.
   - `StartAtValidationTests` — formato, ranges, combinações
     inválidas (sem `--loop-hours`, sem `--telegram-maintain`,
     `loopHours = 0`).
   - `ScheduledJobStartAtConversionTests` — pares
     `(startAtUtc, loopHours) → cron`; casos válidos e
     inválidos (warning quando `loopHours ∉ divisores de 24`).
   - HTTP test: `POST /api/catalog/scheduled-jobs` com
     `startAtUtc + loopHours` → resposta 200 com
     `effectiveCronExpression` correcto.
5. **Build** — `dotnet build m3uCrawler.sln --configuration Release --no-restore`
   → 0 warnings, 0 errors.
6. **Testes** — todos a passar em Release.
7. **Docs afectadas**:
   - `m3uCrawler/README.md` — adicionar `--start-at HH:MM`
     à tabela de argumentos CLI; nova secção "Agendar a
     hora da primeira execução"; cross-reference com o
     tab `Scheduled Jobs`.
   - `docs/IMPLEMENTATION_ROADMAP.md` — actualizar `§32.10`
     (PHASE 12 — entrega) com a nova feature, tabela
     `§32.1` (snapshot por fase), apêndice.
   - `OPERATIONS.md` — nota sobre comportamento de
     `--loop-hours` com e sem `--start-at`.
   - `CHANGELOG.md` — entrada em `[Unreleased]`.
8. **Compatibilidade** — zero impacto em jobs persistidas
   existentes; zero impacto em `--loop-hours` sem
   `--start-at`; zero impacto em modos não-manutenção.

### 16.12 Riscos específicos

| Risco                                                                                       | Mitigação                                                                                                |
|---------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------------|
| Utilizador espera timezone local, não UTC                                                   | Documentação explícita (UTC). Validação rejeita formatos com timezone (`+03:00`). Wave futura pode trazer `CronTimezone` (PHASE 12 b). |
| `loopHours` não divide 24 (ex.: 5, 7) — cron de 5 campos não cobre                          | Warning explícito + fallback ao comportamento actual (relativo). Não bloqueia.                           |
| Job persistida com `CronExpression` ad-hoc conflita com `--start-at` calculado              | Job persistida tem prioridade (lida da BD). CLI só cria a job se **não existir** nenhuma equivalente (mesmo `ActionName`). Idempotente: rodar o processo duas vezes com a mesma config não duplica a job. |
| Drift entre hora UTC do processo e hora UTC real                                            | `DateTime.UtcNow` é a fonte. Se o clock do host driftar, o agendamento drift com ele. (Pré-existente, não introduzido por esta wave.) |
| `Task.Delay` em horas é aproximado (drift de ~ms)                                           | Aceitável para alinhamentos ≥1h. Documentar.                                                              |

---

## 17. Resumo executivo consolidado (wave principal + §16)

| Item                                   | Estado                     |
|----------------------------------------|----------------------------|
| Plano                                  | ✅ este documento          |
| §5–§14 — Observability + trigger       | `[pendente]`               |
| §16 — Scheduled Start Time             | `[pendente]`               |
| Build                                  | `[pendente]`               |
| Testes                                 | `[pendente]`               |
| Docs afectadas actualizadas            | `[pendente]`               |
| Changelog actualizado                  | `[pendente]`               |
| Commit                                 | `[pendente — apenas se pedido]` |

Wave estimada global: **média-a-alta**. Estende o plano
principal com:

- **CLI**: parser `--start-at`, validação,
  `ComputeFirstRunAtUtc` / `ComputeNextRunAtUtc`, branches
  no `RunTelegramMaintenanceCycle` em `Program.cs`.
- **Dashboard**: campo `StartAtUtc` no formulário de
  `ScheduledJob` para a acção `runTelegramMaintainCycle`;
  conversão visível para `EffectiveCronExpression`;
  campo `NextScheduledRunAtUtc` no `/api/run/status`.
- **API**: payload `POST /api/catalog/scheduled-jobs`
  aceita `startAtUtc?`; resposta inclui
  `effectiveCronExpression`.
- **Job persistida por defeito** (se CLI forneceu
  `--start-at --loop-hours`): conversão registada em §16.7.
- **Testes**: 4 novas suites (parser, computação, validação,
  conversão).

Não tocar: tudo o que está na §14 do plano principal +
`CronExpression.cs` (já cobre o formato necessário) +
`ScheduledJobRunner.cs` (mecanismo já existe, basta ligar).

---

## 18. Estado implementado (PHASE 9C.4)

> Esta secção é **normativa sobre o que existe**, não sobre o que foi
> desenhado. O plano acima (§1–§17) é o documento de desenho; onde as
> duas versões divergem, esta secção descreve o comportamento actual e
> identifica o desvio explicitamente.

### 18.1 Modelo persistente

- `live_runs` + `live_run_steps` (`LiveRunEntity` / `LiveRunStepEntity`),
  migration aditiva `AddLiveRuns`. `CountsJson` é a representação
  persistente tipada (`LiveRunCounts`); nenhuma contagem é derivada de
  logs.
- `LiveRun` cobre apenas a execução Telegram. `SyncRun`/`SyncRunStep`
  continuam uma família separada e **não** foram alterados.
- Estados de fase implementados (`LiveRunPhase`): `Idle`,
  `ReadingTelegram`, `Discovering`, `Downloading`, `Analyzing`,
  `Validating`, `Composing`, `SyncingDispatcharr`, `Completed`,
  `Error`.
- `LiveRunTerminalStatus`: `Unknown`, `Completed`, `Failed`.
  `TerminalStatus.Failed` e `LiveRunPhase.Error` são deliberadamente
  conceitos distintos.
- Run interrompido por restart: `FinishedAtUtc == null` **e**
  `TerminalStatus == Unknown` ⇒ recuperado como `Failed` por
  `RunCoordinator.RecoverInterruptedRunsAsync`. Não existe estado
  `Unknown` operacional adicional.

### 18.2 Execução única

- Um único `RunCoordinator` (`LiveRunHost.Coordinator`) serve CLI,
  scheduler e API manual. O lock é um flag atómico
  (`Interlocked.CompareExchange`); um segundo pedido recebe
  `RunAlreadyInProgressException`.
- `StartAsync` (bloqueante) é usado pela CLI e pelo scheduler;
  `KickStartAsync` (não bloqueante, com o mesmo lock) é usado por
  `POST /api/run/start`. Ambos invocam **a mesma** pipeline Telegram
  (`SearchAndTestM3UInTelegramAsync` / `RunTelegramMaintenanceCycle`) —
  não existe segundo pipeline.
- `Source` identifica a origem (`cli`, `manual`, `scheduler`) e `Mode`
  identifica `telegram` / `telegram-maintain`.

### 18.3 API e autenticação

- `GET /api/run/status` — snapshot operacional sanitizado.
  Responde `503 pipeline-not-configured` quando a pipeline Telegram não
  está configurada neste processo (`--web` sem `--telegram`).
  O payload inclui `isRunning`, `status`, `runId`, `mode`, `source`,
  `phase`, `phases`, `phaseStartedAtUtc`, `durationMs`, `counts`,
  `recentActivities`, `recentRuns` e `webAllowTrigger`.
- `POST /api/run/start` — arranque assíncrono. Contrato:
  `202` aceite, `409 already-running`, `503 web-allow-trigger-disabled`,
  `503 pipeline-not-configured`, `400 invalid payload`,
  `401`/`403` conforme o gate 9C.2.
- **Não foi criada autenticação própria.** Reutiliza-se o gate 9C.2
  (`UserAuth`: sessão + CSRF; `--web-token`: credencial de máquina;
  Bootstrap bloqueado; Legacy preservado).
- `--web-allow-trigger` é opt-in (default `false`). Quando ausente,
  `POST /api/run/start` devolve `503 web-allow-trigger-disabled`.

### 18.4 Actividades

- `LiveRunActivityFeed` é um **ring buffer em memória** (capacidade
  200, thread-safe, `LiveRunActivity` com mensagem/metadata já
  sanitizados por `LiveRunSanitizer`). **Não é persistido** em SQLite
  nem em disco: o feed existe apenas enquanto o processo vive e só
  acompanha o run corrente/último run in-process. Esta é a decisão
  implementada na subwave 3 e substitui qualquer formulação anterior
  do plano que sugerisse persistência de actividades.

### 18.5 Scheduler (Scheduled Start Time)

- A integração é feita **no scheduler existente**
  (`ScheduledJobRunner` + `ScheduledAutomationHost`), através de duas
  acções com nomes estáveis:
  `telegramRun` (`Mode=telegram`) e `telegramMaintainRun`
  (`Mode=telegram-maintain`). A corrida scheduler/manual e
  scheduler/scheduler é resolvida pelo lock único do coordinator.
- **Não existe `StartAtUtc`** nem scheduler paralelo. A UI calcula a
  `CronExpression`; o agendamento reutiliza `CronExpression` e a
  tabela `scheduled_jobs` existentes.
- **Cron inválido é rejeitado de forma segura**: o job não executa, é
  neutralizado (`NextRunAtUtc = null`, `LastResult = invalid-cron:…`)
  e o tick continua a processar os restantes jobs.

### 18.6 Dashboard

- Nova vista `view-liverun` ("Live Run"), com estado, runId, fase,
  duração, última actualização, mensagem, contadores, últimas
  actividades, últimas execuções (24 h), estado do trigger, botão
  **Run now** e a lista dos jobs agendados Telegram.
- Actualização automática por **polling leve de 3 s** (apenas com a
  vista activa e sem pedidos sobrepostos), via `GET /api/run/status`.
  **Não há SSE/WebSocket, tail de logs nem parsing de `docker logs`.**
- O `fetch` de mesma origem recebe automaticamente o `X-CSRF-Token`
  injectado na página autenticada (helper 9C.2).

### 18.7 Desvios face ao desenho original

| Desenho (§1–§17) | Implementado (9C.4) |
|---|---|
| `RunProgressSnapshot` / `RunProgressSink` | `LiveRunSnapshot` / `ILiveRunProgress` + `NullLiveRunProgress` |
| Tab "Runs" no Overview + bloco Overview | Vista dedicada `view-liverun` |
| `startAtUtc` + `EffectiveCronExpression` | apenas `CronExpression` (a UI calcula a expressão) |
| Acção `runTelegramCycle` | `telegramRun` + `telegramMaintainRun` |
| Actividades possivelmente persistidas | ring buffer em memória, não persistido |
| `RunId` em `RunReport` (§12.1) | **não implementado**: `RunReport` permanece inalterado (55 propriedades congeladas por teste) |

### 18.8 Não implementado (mantido fora de âmbito)

- Cancelamento de run em curso (§12.3).
- ETA / estimativa de duração (§12.4).
- `StartAtUtc` / hora fixa da primeira execução (§16).
- Qualquer forma de `live-log tail`.