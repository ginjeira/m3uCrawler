# 08 — Validation, Observation e Eligibility

## 1. Observation

Observation é evidência obtida numa execução:
- disponibilidade;
- HTTP/connect result;
- tempo de resposta;
- status;
- qualidade observada;
- EPG;
- outros indicadores.

Observation é histórica e deve poder ser relacionada com um Run.

## 2. Validation

Validation mede o estado técnico da fonte/stream.

Validation não altera:
- CanonicalChannel;
- aliases;
- identidade;
- ordering.

`Observation` é a autoridade do facto técnico observado. Validation é o processo que interpreta/evalua observações segundo policy e não redefine o facto.

## 3. Eligibility

Eligibility é a decisão derivada de observations + policy:

`Eligible | Ineligible | Unknown`

Deve existir evidência suficiente para explicar a decisão.

## 4. Falha

Uma falha de validação não implica por si só apagar ChannelSource.

O sistema deve distinguir:
- falha transitória;
- falha persistente;
- ausência de observação;
- explicitamente removido/desactivado.

## 5. Histerese

Mudanças de estado que provoquem churn devem poder usar uma política de estabilidade/histerese. O comportamento exacto deve ser definido e versionado normativamente antes da implementação da funcionalidade; não é exigido ADR.

**Correspondência normativa.** Validation produz observações/resultados factuais; Eligibility é decisão derivada de evidence + policy. A correspondência entre categorias de Validation e `Eligible | Ineligible | Unknown` é NORMATIVA por categoria; NÃO é uma tabela arbitrariamente configurável pelo operador.

**Recuperação.** Nova evidência válida pode recuperar automaticamente `Ineligible → Eligible`.

**Último estado publicável.** O 'último estado publicável' é o último estado `Eligible` efectivamente publicável dentro da janela de histerese.

**Ausência.** Ausência de observação NÃO significa `Ineligible`.

**Histerese.** Os valores de histerese permanecem parâmetros operacionais (`PARAMETER_GAP`).

## 6. Validação física deduplicada por run (W-DEDUP, 2026-10-01)

**Regra.** No caminho de descoberta Telegram, a validação física de um stream é deduplicada por **ValidationKey** dentro de um run: uma ValidationKey já conhecida `Working` neste run **não** volta a ser GET-testada por outra `AccountKey`. A deduplicação é apenas ao nível do GET físico — **as contas nunca são fundidas** e cada canal conhecido-working continua atribuído à conta que o testou. Esta regra é ortogonal a §2 (Validation não altera identidade), §4 (uma falha não apaga `ChannelSource`) e §5 (ausência de observação ≠ `Ineligible`).

Âmbito: `SearchAndTestM3UInTelegramAsync → TestStreamsAsync → AccountGateCoordinator → AccountValidator.ValidateAccountAsync → M3uTesterService.TestStreamForAccountAsync → ProbeOnceAsync`. O modo manutenção (`--telegram-maintain`), o `ScheduledValidationAction`, o endpoint `/api/validation/test` do dashboard e o Dispatcharr **não** são afectados.

**Identidade usada (ValidationKey = `sfp1`).** A ValidationKey é o fingerprint canónico `sfp1` produzido por `StreamFingerprint.TryComputeFingerprint(url)`, a identidade técnica já ratificada em `04-PLAYLIST-STREAM.md §4.1` (DL-108). Para a forma Xtream dominante `.../live|movie|series/USER/PASS/ID`, `sfp1` mascara `USER`/`PASS` e preserva scheme+host+porta+`ID`, pelo que a ValidationKey funciona como par (endpoint do provider, canal): contas diferentes do mesmo endpoint com o mesmo `ID` colapsam; endpoints diferentes ou `ID` diferentes não colapsam. **Nunca** é usada uma representação sanitizada (`CredentialSanitizer`) como chave — interdição registada em `AGENTS.md` §2 e em `04-PLAYLIST-STREAM.md §4.1`. Não é introduzido conceito `ProviderKey` nem parser de channel-id.

**Probe físico obrigatório por conta.** Cada `AccountKey` elegível (com ≥1 stream) executa **sempre** pelo menos um GET físico com as **suas próprias** credenciais. O índice do probe é escolhido de forma determinística (ordem de parse/input) sobre a composição **completa** da conta, com a preferência: (1) o primeiro stream cuja ValidationKey já seja conhecida `Working` neste run; (2) caso contrário, o primeiro stream com ValidationKey não nula; (3) caso contrário, o primeiro stream elegível. O probe **nunca** é satisfeito pelo resultado de outra `AccountKey`. Racional: testar um canal já conhecido-working separa a falha de conta (credenciais) da falha de canal.

**Falhas nunca são reutilizáveis.** Apenas o estado `Working` é conhecimento reutilizável entre contas. Um resultado `FailedTerminal`/`FailedTransient` de uma conta **nunca** invalida o mesmo canal noutra conta — a falha pode ser específica das credenciais. Resultados transitórios, em progresso, curto-circuitados (`WasShortCircuited`) ou vazios nunca dispensam um GET físico nem são tratados como conhecimento. Ausência de observação de uma conta continua a **não** significar `Ineligible` (§5), e nenhuma falha de validação apaga um `ChannelSource` (§4).

**URLs não fingerprintáveis e Xtream *bare*.** URLs não fingerprintáveis (`TryComputeFingerprint` devolve `null`; ex.: `rtmp://`, `udp://`) são **sempre** testadas fisicamente e **nunca** registadas no registo de dedup. A forma Xtream "bare" (`host:port/user/pass/id`, sem o marcador `/live|movie|series/`) não é mascarada por `sfp1`, pelo que **não** é deduplicada — comportamento deliberado que respeita o fingerprint existente e é registado como limitação conhecida.

**Proveniência e interacção com Observation.** Um resultado reutilizado é um `StreamTestOutcome` sintético com `ReusedKnownWorking = true`, `IsWorking = true`, `Attempts = 0`, `DurationMs = 0` e `HttpStatus = null`. Projecta-se num `M3uStream` com `LastTested = default`; como `PipelineIngestionService` só materializa uma Observation quando `LastTested != default`, **nenhuma observação histórica é fabricada** para um stream reutilizado (§1 mantém-se: Observation é evidência de uma execução real). Streams reutilizados continuam a contar em `working` e a integrar a playlist, pelo que nenhum canal é perdido. `AccountValidationResult` ganha o campo aditivo `Reused` (default `0`).

**Contadores.** `RunReport.StreamsTested` passa a contar **apenas validações físicas**. Os GETs evitados são contados no novo `RunReport.StreamsSkippedAlreadyValidated` (aditivo; espelhado em `LiveRunCounts.StreamsSkippedAlreadyValidated` e exposto como `streamsSkippedAlreadyValidated` por `DashboardMetrics.SummarizeRun`). O invariante `StreamsTested == StreamsWorking + StreamsFailed` (e `testsBalanced`) é preservado. `DiscoveredPlaylist.WorkingStreams` continua a contar todos os streams conhecidos-working da playlist, incluindo os reutilizados.

**Escopo por run e concorrência.** O registo (`ValidationKeyRegistry`) é em memória, de escopo **por run de descoberta Telegram** (nova instância por `SearchAndTestM3UInTelegramAsync`), thread-safe (`ConcurrentDictionary` + `Interlocked`), **não persistido**: sem schema, sem migration, sem escrita em `runtime-data`. O reset é implícito (nova instância); `Reset()` existe para uso explícito. `MaxConcurrentAccounts`, o pool de workers de candidatos, o `AccountGateCoordinator`, os timeouts (`ConnectTimeout`/`ReadTimeout`/`OverallTimeout`), `MaxRetries` e o retry delay **não** mudam. É aceite por desenho uma duplicação de corrida limitada quando duas contas do mesmo endpoint atingem a mesma ValidationKey antes de o primeiro resultado ser publicado — as contas **não** são serializadas.

**Fora de scope.** Sem persistência do registo; sem `ProviderKey`; sem alteração de `sfp1`/`StreamFingerprint`; sem alteração de identidade (`AccountIdentity`), `CredentialSanitizer`, `AccountGateCoordinator`, `CatalogResolver`, `SourceSelectionStage` ou `DispatcharrSyncService`; sem schema/migrations.
