# Auditoria transversal — Conformidade arquitectural e plano de waves
- Data: 2026-09-18
- Estado: Concluída (auditoria; sem commit de código)
- Commits: (nenhum — auditoria read-only)
- BÍBLIA/ADR: registos de implementação (`docs/Reestructure/00-BIBLE.md:30-41`, `:105-128`); auditoria interna (`docs/Reestructure/45-BIBLE-AUDIT.md`, `docs/Reestructure/47-BIBLE-AUDIT-FINDINGS.md`); cadeias de identidade/origem/estado/decisão (`docs/Reestructure/45-BIBLE-AUDIT.md:27-53`)

## Contexto / Objectivo
Interromper a evolução incremental de UI e auditar transversalmente o estado real do repositório para restaurar uma única fonte de verdade por conceito: identidade de canais, país/affinity, ordering/priority/selection, SSOT de configuração e fresh install. Cada conclusão foi classificada como FACT/EVIDENCE/HYPOTHESIS/DECISION/INFERENCE, com evidência `file:line`. A auditoria não alterou código nem produção.

## Âmbito implementado
Auditoria read-only em seis frentes (catálogo/identidade, país/validator, affinity/matching, separação channel/source/ordering, dashboard/config, git/docs), consolidada numa tabela de findings (F-01..F-27, D-F1..D-F16, A1, C-01..C-16) e num plano de waves. Síntese por severidade:

| ID | Finding | Severidade |
|---|---|---|
| A1 | Catálogo canónico PT não empacotado; produção sem generalistas; import silencioso | crítico |
| F-01 | `POST /api/catalog/channels` shadowed (não criava) | crítico |
| F-09/C-04 | `PipelineIngestionService` auto-criava `CanonicalChannel` a partir de `Unknown` | crítico |
| F-02 | `telegramRun` (dashboard/scheduler) não publica/sincroniza | crítico |
| C-01/C-02 | `AnalyzePlaylist` sintetiza identidades a partir de aliases de país | alto |
| C-03/C-16 | Listas PT hardcoded + `countries/*.json` + `channel-indicators.json` duplicam o catálogo | alto |
| C-05 | Membros de affinity de país em store estático process-global | alto |
| C-08/F-21 | Duas raízes `runtime-data` | alto |
| F-03/F-04 | Overrides e `--history-hours` não persistidos/ignorados | alto |
| F-05/F-21 | Fresh install com `--telegram` falha antes do setup | alto |
| F-06 | Gate global exige Telegram para jobs que não precisam | alto |
| F-07/F-25 | Duas fontes de país disjuntas; UI mostra só uma | alto |
| Affinity | Channel Affinity como caminho de identidade paralelo acima de `ChannelAlias` | alto |
| F5-F9 (sep) | `StreamOrderingPolicy` faz preferência de source; 3 mecanismos de priority; 2 implementações de selecção; selection agrupa por `Id` | alto |
| F-11/F-08/F-12 | Review aprovado inerte; `PendingCountryApproval` sem produtor; `sync-runs`/`import-policies`/`groups` sem consumidor; degradação manual | médio |
| F-16/F-17/F-18 | Readiness Dispatcharr não testa ligação; scheduler usa snapshots congelados | médio |
| D-F1..D-F16 | Divergências de separação ordering/priority/selection/identidade | médio/alto |

Plano derivado: Wave A (baseline+create), Wave B (identidade/normalização), W1 (SSOT operacional), W2 (paridade run), W3 (país = classificação), W4 (separação ordering/priority/selection + matching externo), W5 (fresh install ponta-a-ponta), W6 (funcionalidades inertes + audit records), W7 (hardening dashboard), W8 (docs/migrações/E2E).

## Ficheiros/componentes principais
- Nenhum ficheiro de código alterado nesta auditoria.
- Findings registados aqui; correcções distribuídas por A/B/W1..W5.

## Validação (build + suite + testes relevantes)
- n/d — auditoria read-only; baseline no início era 2037 passed / 0 failed / 1 skipped (após Wave B).

## Evidência
- Tabela de findings com evidência `file:line` (síntese acima e registos das waves A/B/W1..W5).
- Correcções: A1/F-01 → `4dff2bf`; F-09/C-04 → `930215a`; F-02/F-06/F-20 → `2f4b662`; F-03/F-04/F-17/F-18 → `1ca9f1e`; C-05/C-10/C-12 → `2ac28d3`; F-05/F-23 → `a2c2eae`; D-F6/D-F8/D-F9 → `fa1b593`; passo 2 do matching → `146b507`.

## Divergências / dívida / follow-ups
- Findings não corrigidos permanecem em `docs/PROJECT_STATUS.md` (Known Gaps/Divergences): F-07/F-08/F-13/F-14/F-15/F-16/F-22/F-27, C-01/C-02/C-03/C-08, D-F1..D-F16, performance e testes flaky.
- W6+ (audit records e wires), W7 (hardening) e o E2E de dois ciclos continuam pendentes.
