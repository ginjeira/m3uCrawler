# Configuration lifecycle (PHASE 9C.1)

> Estado: implementado (PHASE 9C.1). Esta wave estabelece a fundação
> (lifecycle + gates + persistência) sobre a qual o wizard da PHASE 9C.2 será
> construído. Não implementa o wizard, nem redesenha o Dashboard, nem altera
> a política de validação de streams, affinities ou autenticação.

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
