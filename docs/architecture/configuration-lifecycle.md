# Configuration lifecycle (PHASE 9C.1 / 9C.2)

> Estado: PHASE 9C.1 implementada (lifecycle + gates + persistência).
> PHASE 9C.2 implementada (wizard de primeira execução, primeiro
> administrador, sessões persistentes e autenticação normal do Dashboard).
> O wizard é **mínimo** e o `READY` assenta apenas na configuração mínima
> determinística (L2). Não faz redesign do Dashboard, Live Run Monitor,
> SignalR/WebSocket, affinities, Dispatcharr novo, discovery/scheduler
> redesign ou correcção do `AccountGateCoordinator`.

## Objectivo

Uma instalação nova deve ser **fail-safe**:

```
application starts
      ↓
Dashboard available
      ↓
NOT_CONFIGURED
      ↓
NO automatic discovery
NO automatic scheduled jobs
```

O administrador acede ao Dashboard e, numa wave posterior, conclui a
configuração mínima. Até lá, nenhuma execução automática operacional corre.

## Estados

`ConfigurationLifecycleState` (persistido e reportado como string):

| Estado | Wire name | Discovery automático | Scheduler |
|---|---|---|---|
| `NotConfigured` | `NOT_CONFIGURED` | bloqueado | bloqueado |
| `Configuring` | `CONFIGURING` | bloqueado | bloqueado |
| `Ready` | `READY` | permitido | permitido |

Não há `RUNNING`/`ERROR` nesta wave: a máquina de estados completa não é
necessária para o objectivo fail-safe.

## Autoridade do estado persistido

O estado persistido é a **autoridade**. Se existir um estado persistido válido,
é usado sem qualquer inferência e sem reavaliação de evidência.

Só quando **não** existe estado persistido é que o bootstrap corre:

1. procurar evidência objectiva e verificável de instalação já operacional;
2. se existir evidência, adoptar `READY` com `AdoptedFromLegacy=true` e registar
   a razão (nomes das evidências) — **legacy adoption**;
3. caso contrário, iniciar em `NOT_CONFIGURED`.

## Critério de legacy adoption

Evidência baseada em entidades que uma instalação nova **não** cria
(migration/seed/baseline):

- `sources`, `channel_sources`, `channel_source_observations`
- `ordering_lists`, `ordering_items`
- `import_policies`
- `canonical_groups`, `group_mappings`
- `scheduled_jobs`
- `sync_runs`, `sync_run_steps`
- `review_items`
- `matching_audits`
- `pending_country_approvals`
- `affinity_groups` (e membros)
- `dispatcharr_channel_ownerships`, `dispatcharr_stream_ownerships`
- `identity_rules` (o seed é vazio, pelo que qualquer linha é criada por operação)

Evidência baseada em artefactos de output:

- `import_history.json`
- `playlist.m3u`
- `telegram_run_report.json`
- `telegram_playlist_*.m3u`

Excluídos deliberadamente (existem numa instalação nova):

- `canonical_channels` e `channel_aliases` (seed + baseline import);
- `source_priority_policies` (default global criado lazily por
  `GetOrCreateGlobalPriorityPolicyAsync`).

Qualquer falha a ler a BD ou o output é tratada como "sem evidência"
(fail-safe): nunca se adopta `READY` com base em leitura parcial.

## Persistência

- Ficheiro `configuration_lifecycle.json`, no mesmo directório do
  `channel-catalog.db` (volume persistente; em produção `/data`).
- Reutiliza o padrão já existente de estado persistente em JSON
  (`stream_validation_policy.json`, `countries/*.json`), evitando uma tabela
  nova no schema do catálogo. Não há migração de BD nesta wave.
- Escrita atómica (ficheiro temporário + `File.Move` com overwrite).
- Ficheiro ilegível/corrompido → o bootstrap volta a correr.

## Gates

`IConfigurationGate` é o **mecanismo único** de decisão (evita espalhar
`if (!configured)` por múltiplos locais). `ConfigurationGate` apoia-se no
lifecycle service e trata qualquer estado desconhecido como não-pronto.

- `ScheduledJobRunner` (scheduler + todas as `IScheduledAction`, incluindo
  `discoverM3u`): em `NOT_CONFIGURED`/`CONFIGURING`, nenhum job executa. Os jobs
  vencidos ficam com `LastResult = "blocked:not-configured"`, sem `LastRunAtUtc`
  nem avanço de `NextRunAtUtc` — continuam vencidos para correr assim que `READY`.
- Telegram automático: `RunTelegramMaintenanceCycle` (`--telegram-maintain`) e o
  loop (`--loop-hours`) são bloqueados se o estado não for `READY`. Uma
  invocação manual de um único ciclo não é afectada nesta wave.

## Compatibilidade

- Instalação existente operacional (com evidência) → adopta `READY` no primeiro
  arranque pós-9C.1; discovery e scheduler continuam a funcionar.
- Instalação existente sem evidência suficiente → `NOT_CONFIGURED` (bloqueada,
  fail-safe).
- Instalação nova → `NOT_CONFIGURED`; Dashboard acessível.
- Sem migração destrutiva e sem alteração do schema da BD do catálogo.

## Requisitos PHASE 9C §32.18 §5 (advisory)

Os requisitos completos de configuração (ordering list operacional, políticas de
país/importação, grupos, sources activas, source priority, validação de streams,
etc.) são **advisory** nesta wave:

- não são gate de `READY`;
- são expostos em `GET /api/configuration/lifecycle`;
- só são avaliados quando determináveis de forma segura a partir do estado
  existente (catálogo canónico, ordering list, import policies, grupos
  canónicos, sources activas, source priority, jobs agendados, output dir).

Torná-los obrigatórios bloquearia instalações existentes que nunca os
configuraram, o que viola o requisito de compatibilidade. A sua conversão em
gate pertence ao wizard (PHASE 9C.2).

## Observabilidade

- `🧭 Configuration lifecycle: <ESTADO>` no arranque; com
  `(legacy adoption: <evidências>)` quando aplicável.
- `⛔ automatic discovery blocked: not configured (state=…)`.
- `⛔ scheduler blocked: not configured (state=…)`.

Nunca são registadas credenciais, passwords, tokens ou URLs sensíveis.

## Ficheiros

- `m3uCrawler/Services/Configuration/ConfigurationLifecycleState.cs`
- `m3uCrawler/Services/Configuration/ConfigurationLifecycleStore.cs`
- `m3uCrawler/Services/Configuration/LegacyConfigurationEvidenceEvaluator.cs`
- `m3uCrawler/Services/Configuration/ConfigurationLifecycleService.cs`
- `m3uCrawler/Services/Configuration/ConfigurationGate.cs`
- Wiring: `m3uCrawler/Program.cs`, `m3uCrawler/Services/Automation/ScheduledJobRunner.cs`,
  `m3uCrawler/Services/Automation/ScheduledAutomationHost.cs`,
  `m3uCrawler/Services/WebDashboardService.cs`
- Testes: `m3uCrawler.Tests/ConfigurationLifecycleTests.cs`,
  `m3uCrawler.Tests/ConfigurationGateSchedulerTests.cs`,
  `m3uCrawler.Tests/ConfigurationLifecycleEndpointTests.cs`

---

# Autenticação e bootstrap (PHASE 9C.2)

## Modelo de dados

- `admin_users` — `Id` (PK), `Username` (unique, ≤64), `PasswordHash` (PHC, ≤256),
  `IsEnabled`, `CreatedAtUtc`, `UpdatedAtUtc`, `LastLoginAtUtc`.
  Nesta wave todas as linhas são administrador; não há role/claims.
- `admin_sessions` — `Id` (PK), `SessionId` (unique, opaco 256 bits),
  `AdminUserId` (FK→`admin_users`, cascade), `CsrfToken`, `CreatedAtUtc`,
  `ExpiresAtUtc`, `LastSeenAtUtc`.
- Migration aditiva `AddAdminUsersAndSessions` (apenas cria as duas tabelas e
  índices; não altera/remove dados existentes).

## Passwords

- PBKDF2-HMAC-SHA256 nativo (`Rfc2898DeriveBytes`), 210 000 iterações,
  salt 16 bytes, hash 32 bytes.
- Formato persistido versionado:
  `pbkdf2-sha256$<iterations>$<base64(salt)>$<base64(hash)>` — permite evolução
  futura (mais iterações ou outro algoritmo) sem migration de dados.
- Política: mínimo **12 caracteres**; sem regras artificiais de complexidade;
  rejeita vazia, whitespace-only e > 256.
- Utilizador inexistente/inactivo executa derivação **dummy** (tempo uniforme,
  sem enumeração); comparação em tempo constante (`FixedTimeEquals`).
- Password/hash nunca aparecem em logs, respostas ou erros.

## Sessões

- Cookie `m3u_session` com **apenas** um id opaco (256 bits); nunca username,
  password ou hash.
- `HttpOnly`, `SameSite=Strict`, `Path=/`, e `Secure` **apenas quando HTTPS**
  (o deployment HTTP actual não protege credenciais em trânsito — exposição fora
  de rede confiável deve usar reverse proxy/TLS).
- Store server-side em SQLite (`admin_sessions`): sobrevive a restart, é
  revogável (logout), expira (janela deslizante de 12 h, tecto absoluto de 12 h)
  e é validada contra o administrador activo.
- Rotação de id em cada login (anti session-fixation).
- CSRF: token por sessão, exigido no header `X-CSRF-Token` em métodos mutantes
  (login e endpoints de bootstrap são isentos).

## Fluxo de bootstrap

```
NOT_CONFIGURED
  → GET /bootstrap                (página mínima)
  → POST /api/bootstrap/start     → CONFIGURING
  → POST /api/bootstrap/admin     → cria o 1.º administrador (transaccional)
  → POST /api/bootstrap/complete  → valida L2 → READY
```

- Serialização in-process (`SemaphoreSlim`) + transacção EF: nunca dois
  primeiros administradores; retry idempotente (`AlreadyCreated`).
- Invariantes: nunca `READY` sem administrador activo nem sem L2 válida;
  `READY` só depois da criação e validação; restaurar em `CONFIGURING` retoma
  (a existência do admin determina o passo); bootstrap fechado após `READY`.
- `POST /api/bootstrap/admin` nunca devolve a password; apenas a chave de erro.

## Configuração mínima (L2) para READY

| Item | Obrigatório |
|---|---|
| Administrador activo | Sim |
| Catálogo canónico utilizável (`canonical_channels` com canais) | Sim |
| Output directory utilizável (criável/gravável, com probe real) | Sim |
| Dispatcharr válido (`BaseUrl` + apiKey ou user/pass) **se activado** | Condicional |
| Telegram, sources, ordering, import policies, grupos, source priority, scheduler | Não (advisory) |

Nota: o loader existente normaliza `dispatcharr_enabled=true` sem
`dispatcharr_base_url` para "desactivado"; esse caso não bloqueia `READY`.

## Modos de autorização

| Modo | Condição | Comportamento |
|---|---|---|
| **Bootstrap** | `NOT_CONFIGURED`/`CONFIGURING` | Só `/`, `/bootstrap`, `/api/bootstrap/*`, `/api/session`, `/api/version` e `/api/configuration/lifecycle`; restantes endpoints → `403 bootstrap-required` |
| **UserAuth** | `READY` ∧ admin activo | Endpoints normais exigem **sessão humana** (ou credencial de máquina válida); mutantes exigem CSRF; `/` sem sessão serve página de login |
| **Legacy** | `READY` ∧ sem admin | Mantém o comportamento actual baseado só em `--web-token`; não cria admin nem migra |

### Precedência `--web-token` vs sessão humana

`--web-token`, quando configurado, é exigido em **todos** os modos (é avaliado
antes de qualquer rota). Dentro disso:

- **token válido** → autoriza o pedido, **sem** exigir sessão humana, incluindo
  em `READY` + admin (`UserAuth`). É uma credencial de **máquina** para automação;
  não cria utilizador nem sessão e não é uma password de utilizador.
- **token ausente/inválido** com `--web-token` configurado → `401` (gate de token).
- **sem `--web-token` configurado** em `UserAuth` → exige sessão humana e CSRF.
- **token válido + sessão presente** → comportamento determinístico: o pedido é
  autorizado (a credencial de máquina é suficiente); CSRF não é exigido para
  pedidos autenticados por token, por não serem CSRF-able.

Numa instalação nova `--web-token` não é necessário, mas não é proibido.

### CSRF e a UI

A protecção CSRF do servidor mantém-se (`X-CSRF-Token` obrigatório em métodos
mutantes autenticados por sessão). A página autenticada do Dashboard recebe o
token de sessão **apenas em memória JavaScript** (nunca em URL, query,
`localStorage` ou logs) e injecta um helper que adiciona automaticamente o header
a todos os `fetch` de mesma origem. Sem sessão humana (legacy/bootstrap) o helper
não é injectado.

### Falha de inicialização do auth (fail-closed)

Se o `AuthService` não puder ser inicializado, o modo **não** cai para `Legacy`
(autorização implícita). Em `READY` o gate exige autenticação e devolve `401`
(fail-closed); fora de `READY` mantém-se em bootstrap. Nunca há acesso
administrativo anónimo por falha de wiring.

Testes de referência: `AuthPrimitivesTests`, `AdminSessionStoreTests`,
`BootstrapServiceTests`, `BootstrapConfigurationValidatorTests`,
`DashboardBootstrapEndpointTests`.
