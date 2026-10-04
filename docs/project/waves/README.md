# Waves de implementação — índice

Registos históricos das waves de implementação do m3uCrawler e respectiva evidência.
Documentação derivada, subordinada à BÍBLIA (`docs/Reestructure/`); não redefine conceitos
normativos (`docs/Reestructure/00-BIBLE.md:30-41`, `:105-128`).

| # | Wave | Data | Estado | Commits | Validação |
|---|---|---|---|---|---|
| 1 | [13-6 — Sanitização de credenciais no relatório Dispatcharr](2026-09-18-13-6-report-credential-sanitization.md) | 2026-09-18 | Concluída | 26c91cd | 1917/0/1; 0 err/52 warn |
| 2 | [CI — Classificação Linux de connection refused](2026-09-18-ci-connection-refused-classification.md) | 2026-09-18 | Concluída | 69b7779 | CI 35369150660 success; 1917/0/1 |
| 3 | [Onboarding — Setup operacional de primeira execução](2026-09-18-onboarding-operational-setup.md) | 2026-09-18 | Concluída | 39383fc..99a2818 | 1927→1994/0/1; 0/52 |
| 4 | [Admin Password — Gestão e recuperação](2026-09-18-admin-password-management.md) | 2026-09-18 | Concluída | 419e23a, ab595a3, 899a030 | 2013/0/1 (flaky isolado) |
| 5 | [Audit Wave A — Baseline canónico PT](2026-09-18-audit-wave-a-canonical-baseline.md) | 2026-09-18 | Concluída | 4dff2bf | 2023/0/1; 0/52 |
| 6 | [Audit Wave B — Identidade de canal](2026-09-18-audit-wave-b-channel-identity.md) | 2026-09-18 | Concluída | 930215a | 2037/0/1; 0/52 |
| 7 | [Auditoria transversal — Findings e plano de waves](2026-09-18-audit-transversal-findings.md) | 2026-09-18 | Concluída (read-only) | (nenhum) | n/d |
| 8 | [W1 — Discovery settings SSOT](2026-09-18-w1-discovery-settings-ssot.md) | 2026-09-18 | Concluída | 1ca9f1e | 2063/1 flaky/1; 0/52 |
| 9 | [W2 — Paridade de execução](2026-09-19-w2-run-parity.md) | 2026-09-19 | Concluída | 2f4b662 | 2070/0/1; 0/52 |
| 10 | [W3s — País como classificação](2026-09-19-w3s-country-classification.md) | 2026-09-19 | Concluída | 2ac28d3 | 2090/0/1; 51 warn |
| 11 | [ADRs — Decisões abertas que exigem ADR](2026-09-19-adrs-open-decisions.md) | 2026-09-19 | Concluída (docs-only) | 170acac | n/d |
| 12 | [W4a — Selection DL-101](2026-09-19-w4a-selection-dl101.md) | 2026-09-19 | Concluída | fa1b593 | 2104/1 flaky/1; 52 warn |
| 13 | [W4b — Identidade externa (tvg-id)](2026-09-19-w4b-external-identity.md) | 2026-09-19 | Concluída | 146b507 | 2131/0/1; 52 warn |
| 14 | [W5 — Fresh install sem Telegram](2026-09-19-w5-fresh-install.md) | 2026-09-19 | Concluída | a2c2eae | 2136/1 flaky/1 (isolado 9/9); 52 warn |
| 15 | [ADR-0001 — Completude do ADR de país](2026-09-19-adr-0001-completion.md) | 2026-09-19 | Concluída (docs-only) | 4de2773 | n/d |
| 16 | [W6a — Audit records](2026-09-19-w6a-audit-records.md) | 2026-09-19 | Concluída | 39eefe7 | 2148/0/1 |
| 17 | [W6b-1 — Aprovação de Review](2026-09-19-w6b1-review-approval.md) | 2026-09-19 | Concluída | db9959b | 2162/0/1 |
| 18 | [W6b-2 — Observabilidade](2026-09-19-w6b2-observability.md) | 2026-09-19 | Concluída | ee7baab | 2170/0/1; 51 warn |
| 19 | [W6c — Config Dispatcharr, readiness e selection](2026-09-19-w6c-dispatcharr-config-readiness-selection.md) | 2026-09-19 | Concluída | b19d95b | 2186/0/1; 51 warn |

W6b-3 (import-policies, canonical-groups/group-mappings, pending-country-approvals) está definida e pendente — ver `docs/PROJECT_STATUS.md`.
O E2E de dois ciclos (idempotência Discovery→…→Dispatcharr) é o próximo passo executável.

> **Nota (2026-09-20).** O índice acima cobre apenas as waves da fase 9c e termina em `W6c`
> (`b19d95b`, 2026-09-19); está correcto enquanto histórico dessa fase e não é reescrito.
> O `HEAD` real do repositório é agora `780fa0148617c9074146f028cd2fcb61b3a9b50d` (`780fa01`,
> 2026-09-20); as waves de reconstrução `W1–W5.6` foram implementadas depois do conjunto indexado
> e estão registadas na secção seguinte. `docs/PROJECT_STATUS.md` foi reconciliado com este estado.

## Reconstruction waves (W1–W5.6) — 2026-09-20

Evidência detalhada: `docs/Reestructure/46-REQUIREMENT-TRACEABILITY.md` (secções W2–W5.6) e
`BIBLE_IMPLEMENTABILITY_GAP_MANIFEST_1.0.md` (Anexos G–S.2). Esta secção é registo documental;
não ratifica decisões — `M.4` permanece **OPEN**.

| Wave | Scope | Commit | Evidence/Notes |
|---|---|---|---|
| W1 | Discovery identity | `ab3f817` | Identidade de discovery no pipeline. |
| W2 | Acquisition security | `ab3f817` | **Debt W2-FU:** o observer não está ligado em produção. |
| W3 | M3U parsing contract | `ab3f817` | Contrato de parsing M3U. |
| W4 | Stream normalization + `sfp1` fingerprint | `c73e134` | Normalização de streams e fingerprint `sfp1`. |
| W4.1 | Fingerprint-aware source selection resolution | `8142c0d` | Resolução de selecção de fonte ciente do fingerprint (fix 13). |
| W5.0 | Base das waves 5.x (scoping/ordenação) | (ver W5.1) | Sem commit isolado; coberta pela evidência em `46`. |
| W5.1 | RecognitionPolicy (scoped resolution + run snapshot) | `5236364` | **Debt:** sem integração em produção. |
| W5.2 | Deterministic recognition order | `4721dc3` | Ordem de reconhecimento determinística. |
| W5.3 | Fuzzy recognition | `84b35f5` | **Dependente de `M.4`:** produção chama `ResolveAsync` com `policy: null`. |
| W5.4 | Review lifecycle | `84b35f5` | Ciclo de vida de Review. **Debt:** Review→Output não ligado. |
| W5.5 | Review HTTP API | `b8c9cda` | **Resolve gap:** `externalIdentity`/`channelSource`/`none` → 422. |
| W5.6 | MatchMethod/MatchConfidence | `b613502` | Semântica de match method/confidence. |
| W5.6-FU | Follow-up F7-B/F8-B/F9 | `780fa01` | `HEAD` actual; follow-up da W5.6. |

**Debt funcional a manter visível:** W2-FU (observer não ligado em produção); W5.1 sem integração
em produção; W5.3 dependente de `M.4` (produção chama `ResolveAsync` com `policy: null`); W5.5
resolve gap (`externalIdentity`/`channelSource`/`none` → 422); Review→Output não ligado;
`C7` **OPEN**; `M.4` **OPEN**.

## Plano de recuperação pós-auditoria runtime (2026-10-04) — execução

A auditoria runtime de 2026-10-04 (HEAD `cf0e314`, runtime `m3ucrawler-first-test`) e o plano de
waves de recuperação `W1–W7` estão em
[`2026-10-04-runtime-audit-dashboard-recovery-plan.md`](2026-10-04-runtime-audit-dashboard-recovery-plan.md).
Registo de **execução**: `W1`, `W2` e `W3` implementadas (2026-10-04); `W4–W7` pendentes. Próxima
prioridade: **W4** (Scheduler + Ordering + Canonical Channels). Estado corrente em `docs/PROJECT_STATUS.md`.
