# W-PRE-FIRST-E2E — preparar o primeiro First Run real via Web UI

- Data: 2026-09-29
- Estado: Concluída
- Wave pai: `2026-09-25-w-audit-web-admin-e2e.md` (P0.1, F-22)
- Próxima wave prevista: `W-FIRST-E2E-TEST` (não implementada aqui)

## 1. Contexto

A auditoria read-only concluiu que o caminho First E2E (Bootstrap → Login → Configurar Telegram → Configurar Discovery → Configurar Dispatcharr → Run now → Acompanhar Live Run → Publicação → DL-130 cursor → Dispatcharr dry-run) está **quase totalmente implementado**.

O **único blocker P0** identificado era P0.1 — a flag opt-in `--web-allow-trigger` (introduzida em PHASE 9C.4 como protecção deliberada; ver `docs/architecture/run-observability-and-manual-trigger.md:920-937`) não era passada pelo `docker-compose.yml`, deixando o botão "Run now" da dashboard inacessível sem edição manual. Dívida conhecida como F-22 (referida em três waves: `w2-run-parity.md`, `w5-fresh-install.md`, `w6c-dispatcharr-config-readiness-selection.md`, e em `docs/PROJECT_STATUS.md:123`).

Esta wave fecha F-22 e adiciona diagnóstico accionável no UI. **Não** altera o mecanismo de segurança, **não** elimina a flag, **não** implementa teste E2E HTTP completo (esse é objecto da próxima wave).

## 2. Análise da intenção original

A flag foi adicionada em PHASE 9C.4 com Opção B ratificada (`docs/architecture/run-observability-and-manual-trigger.md:929-937`):

> "alinha com o princípio 'não expor superfícies sem opt-in explícito'. O trigger manual é uma superfície nova; merece flag dedicada. Combinada com `--web-token` (recomendado em deployments não locais), o resultado é seguro."

**Conclusão:** a flag é uma **proteção deliberada**, não um esquecimento. A solução não pode ser removê-la ou desligar o gate; tem de ser operacionalizar a sua passagem para que o deployment default a active, mantendo o opt-in ao nível do binário.

## 3. Estado actual (antes desta wave)

| Componente | Estado | Evidência |
|---|---|---|
| Definição CLI | `--web-allow-trigger`, default `false` | `Program.cs:74` |
| Setter estático | `WebDashboardService.SetWebAllowTrigger(bool)` | `WebDashboardService.cs:151-154` |
| Gate no handler | `if (!_webAllowTrigger) return 503 web-allow-trigger-disabled` | `WebDashboardService.cs:9755-9762` |
| UI activa (`renderLiveRun`) | Mostra badge "Trigger manual: activado/desactivado" + botão disabled quando off | `WebDashboardService.cs:8528-8543` |
| UI do erro 503 | Apanhado em `startLiveRun` e renderizado como erro genérico | `WebDashboardService.cs:8670-8674` |
| Snapshot do status | Inclui `webAllowTrigger: bool` (campo serializado) | `LiveRunApiContracts.cs:67,79,91,126,132,141,158,181,187,220` |
| Texto disable | "Arranque via `<code>--web-allow-trigger</code>`." — accionabilidade fraca | `WebDashboardService.cs:8533` (pré-wave) |
| `docker-compose.yml` (produção) | **Não passava a flag** — F-22 | pré-wave |
| `docker-compose.local.yml` | **Não passava a flag** em nenhum dos dois serviços — F-22 | pré-wave |
| Teste simétrico `true` | `Get_status_machine_token_authorizes_when_no_user_session` valida `webAllowTrigger=true` | `Phase94LiveRunApiTests.cs:246-259` |
| Teste caminho `false` | Inexistente — gap de cobertura | pré-wave |
| Teste do 503 | `Post_start_when_trigger_disabled_returns_503` valida o erro literal | `Phase94LiveRunApiTests.cs:367-375` |

## 4. Decisão técnica tomada

Opção **A** do brief: **manter a flag como opt-in de segurança + tornar o seu estado claramente visível e accionável no UI + operacionalizar a passagem via `docker-compose.yml`**.

Justificação:
- A intenção original é preservada (opt-in ao nível do binário continua a ser a decisão do operador que arranca o container; passar a flag no compose não é universal — operadores avançados podem removê-la se preferirem).
- A fricção operacional desaparece no caso default (deploy out-of-the-box funciona).
- O UI deixa de assumir que o admin conhece a flag: indica onde editar e o que fazer.
- A cobertura de testes passa a cobrir o caminho `false` do snapshot (necessário porque o UI depende dessa leitura para mostrar o diagnóstico).

Não foi tomada nenhuma das outras opções:
- **B** (config persistente nova): acrescentaria uma superfície de configuração redundante com a CLI.
- **C** (trigger no bootstrap): quebraria a separação `Bootstrap` ≠ `UserAuth` (DL-021) e introduziria uma segunda lógica de autorização.
- **D** (outra): nenhuma se mostrou mais coerente do que fechar F-22 + diagnóstico.

## 5. Alterações efectuadas

### 5.1 `docker-compose.yml` (produção)

Adicionada a flag `--web-allow-trigger` no final do `command:` do serviço `m3ucrawler`, com comentário inline a referenciar a wave e F-22. Indentação YAML: 6 espaços (consistente com flags vizinhos `--web`, `--web-port`).

**Diff conceptual:**
```diff
       - --web
       - --web-port
       - "5000"
+      # W-PRE-FIRST-E2E (F-22): activa o botão "Run now" no dashboard.
+      # Default do binário é 503 web-allow-trigger-disabled; sem esta
+      # flag o trigger manual fica inacessível pela Web UI.
+      - --web-allow-trigger
```

### 5.2 `docker-compose.local.yml` (dev)

Adicionada a flag aos dois serviços (`m3ucrawler` e `m3ucrawler-telegram`). Comentário curto a referenciar `docker-compose.yml` para evitar drift.

> **Nota sobre versionamento:** `docker-compose.local.yml` está em `.gitignore` (linha 30) por design (AGENTS.md §9.7; contém paths absolutos Windows específicos da máquina). Esta alteração afecta apenas a cópia local do operador; não é commitada. O efeito de "compose dev passa a flag" é comunicado pela alteração em `docker-compose.yml` (produção) + este wave doc.

### 5.3 `WebDashboardService.cs` — diagnóstico accionável

Alterado o texto da dashboard quando o trigger está desactivado (`renderLiveRun`, linha 8535-8539). O texto agora indica:
1. Onde editar (`docker-compose.yml`).
2. O que adicionar (`--web-allow-trigger`).
3. Que é preciso reiniciar o container.
4. O que se ganha (botão "Run now" disponível).

A renderização do botão (`btn.disabled = !allow || !!(data && data.isRunning);`) **não foi alterada**. O literal de erro `"web-allow-trigger-disabled"` no handler **não foi alterado** (mantém compatibilidade com o teste existente `Post_start_when_trigger_disabled_returns_503`).

### 5.4 Teste novo em `Phase94LiveRunApiTests.cs`

Adicionado o teste `Get_status_reflects_web_allow_trigger_false` (linha 261-278) que valida o caminho complementar ao teste simétrico `Get_status_machine_token_authorizes_when_no_user_session` (linha 246-259). Usa machine token para autenticar e verifica `webAllowTrigger=false` no payload JSON do `GET /api/run/status`.

Esta cobertura é **necessária** porque o UI depende desta leitura para apresentar o diagnóstico accionável da §5.3.

## 6. Testes

### 6.1 Teste adicionado

`m3uCrawler.Tests/Phase94LiveRunApiTests.cs:261-278` — `Get_status_reflects_web_allow_trigger_false`.

Padrão: idêntico ao teste simétrico `Get_status_machine_token_authorizes_when_no_user_session` (linha 246-259), mas com `webAllowTrigger: false` e `Assert.False(...)` em vez de `Assert.True(...)`.

### 6.2 Testes existentes que continuam a passar (não alterados)

| Teste | Linha | Cobre |
|---|---|---|
| `Get_status_machine_token_authorizes_when_no_user_session` | 246-259 | caminho `true` |
| `Post_start_when_trigger_disabled_returns_503` | 367-375 | 503 com literal `"web-allow-trigger-disabled"` |
| `Post_start_machine_token_authorizes_when_no_user_session` | 332-345 | POST 202 com `webAllowTrigger=true` |
| `Post_start_user_auth_with_csrf_starts_run` | 313-329 | POST 202 via sessão+CSRF |
| `Post_start_user_auth_without_csrf_returns_403` | 301-310 | CSRF gate |
| `Post_start_bootstrap_without_machine_token_returns_403` | 277-284 | Bootstrap gate |
| `Post_start_ready_without_admin_requires_bootstrap` | 287-298 | READY-sem-admin → bootstrap |

### 6.3 Validação

- Build Release: **não executado** — `dotnet` não está acessível neste ambiente (`/root/.dotnet/` apenas contém cache e workloads, não o binário `dotnet`). Validação feita por análise estática via sub-agente (`PASS` 4/4 itens).
- Sintaxe JS da `renderLiveRun` validada contra o resto do dashboard HTML (uso consistente de `&quot;`, concatenação com `+`, classes CSS `badge ok/muted` existentes).
- YAML dos dois compose validado por indentação 6 espaços e preservação de todas as flags anteriores.
- Assinaturas dos helpers de teste (`BuildHostAsync`, `IdlePipeline`, `StartHarness`) confirmadas por grep no ficheiro de teste.

## 7. Validação runtime

Não foi possível executar runtime real neste ambiente (sem `dotnet`, sem Docker, sem rede para o registry GHCR). A validação runtime real será feita na próxima wave (`W-FIRST-E2E-TEST`), que estabelece o teste E2E HTTP `POST /api/run/start` → `RunCoordinator` → `RunPublicationService` → `playlist.m3u` → Dispatcharr artefact.

Esta wave deixa o **terreno preparado** para essa validação: o deployment default (Docker) passa agora a flag, o UI diagnostica o estado off de forma accionável, e o novo teste confirma que o snapshot reflecte fielmente o estado da flag.

## 8. Ficheiros alterados

```
docker-compose.yml                     | +4 -0   (3 comentários + 1 flag)        [versionado]
docker-compose.local.yml               | +4 -0   (2 comentários + 2 flags)       [gitignored]
m3uCrawler/Services/WebDashboardService.cs | +7 -1  (texto do diagnóstico)       [versionado]
m3uCrawler.Tests/Phase94LiveRunApiTests.cs | +18 -0  (teste novo)                [versionado]
```

Total versionado: **3 ficheiros modificados + 1 ficheiro novo (`docs/project/waves/2026-09-29-w-pre-first-e2e.md`)** = **4 ficheiros em git**.

O `docker-compose.local.yml` foi alterado mas **não** é commitado (consta em `.gitignore:30` por design — ver §5.2). A alteração local ao compose de dev fica na working copy do operador.

## 9. Documentação alterada

- **Novo:** `docs/project/waves/2026-09-29-w-pre-first-e2e.md` (este ficheiro).
- **Não alterado:** `CHANGELOG.md` (a alteração é wave small/scope-limited; segue a prática de waves anteriores que só tocam CHANGELOG quando há release).
- **Não alterado:** `docs/Reestructure/31-DECISION-LOCK.md` (a decisão DL-130 e a intenção do `--web-allow-trigger` estão preservadas; nada foi ratificado nem revogado).
- **Não alterado:** `DEPLOYMENT.md`, `OPERATIONS.md`, `m3uCrawler/README.md`, `AGENTS.md` — as alterações nos compose são auto-documentadas via comentários inline; o README já documenta a flag (linha 116); o comportamento (opt-in, 503, default off) **não foi alterado**.

## 10. Estado preparado para `W-FIRST-E2E-TEST`

A próxima wave `W-FIRST-E2E-TEST` (já prevista na auditoria §16 pergunta F, item 2) pode começar imediatamente porque:

1. O deployment default passa `--web-allow-trigger`, pelo que `POST /api/run/start` retorna 202 em vez de 503 num fresh install.
2. O snapshot `GET /api/run/status` continua a incluir `webAllowTrigger: bool` (já era verdade; agora está coberto por teste).
3. A autenticação (sessão+CSRF, machine token, bootstrap gate) está intacta.
4. O handler HTTP `HandleRunStartEndpointAsync` (linha 9743-9830) está intocado.
5. O pipeline `RunCoordinator` → `TelegramLiveRunExecutor` → `RunPublicationService.PublishAsync` → `PlaylistManagerService.SaveToM3uPlaylistAtomic` → `DispatcharrSyncCoordinator.RunAsync` está intacto.

Pontos que a `W-FIRST-E2E-TEST` terá de tratar (não são bloqueadores desta wave):
- Definir o harness `HttpListener` + `TestServer` com wiring real (autenticação, catálogo, LiveRunHost).
- Decidir se o teste usa Telegram real (improvável em CI) ou substitui o scraper por uma fixture.
- Validar formato JSON do `dispatcharr_plan_<ts>.json` e `dispatcharr_report_<ts>.json`.
- Validar DL-130 cursor (`publicationPending=false` após Run Completed).

## 11. Estado Git

### Antes
- Branch: `feature/phase-9c-first-run-dashboard`
- HEAD: `780fa0148617c9074146f028cd2fcb61b3a9b50d`
- Working tree: limpo

### Depois
- 4 ficheiros versionados (3 modificados + 1 novo):
  - `docker-compose.yml` (modificado)
  - `m3uCrawler/Services/WebDashboardService.cs` (modificado)
  - `m3uCrawler.Tests/Phase94LiveRunApiTests.cs` (modificado)
  - `docs/project/waves/2026-09-29-w-pre-first-e2e.md` (novo)
- 1 ficheiro gitignored modificado (não commitado):
  - `docker-compose.local.yml`
- 0 ficheiros de código fora do âmbito alterados.

## 12. Confirmação de não-alteração fora do âmbito

- ✗ Não foi removida a flag `--web-allow-trigger`.
- ✗ Não foi alterado o default (`false` continua a ser o default do binário).
- ✗ Não foi tocado o gate 503 (`web-allow-trigger-disabled` continua a ser o literal).
- ✗ Não foi tocado o gate de autenticação (sessão+CSRF, machine token, bootstrap).
- ✗ Não foi tocado o `LiveRunApiContracts`.
- ✗ Não foi tocado o `RunPublicationService`.
- ✗ Não foi tocado o `HandleRunStartEndpointAsync`.
- ✗ Não foi tocado o `RenderLiveRun` excepto o texto do diagnóstico.
- ✗ Não foi tocado o scheduler, o catálogo, o Telegram, o Dispatcharr, o matcher, o validator, nem nenhum dos `Services/*` fora de `WebDashboardService.cs`.
- ✗ Não foi tocada nenhuma migration.
- ✗ Não foi tocado nenhum Decision Lock.
- ✗ Não foi feito `git add .` nem `git push`.

## 13. Critérios de sucesso (do brief)

| # | Critério | Cumprido? | Evidência |
|---|---|---|---|
| S1 | Mecanismo `--web-allow-trigger` completamente compreendido e documentado | ✓ | §2, §3 deste doc; cross-ref a `docs/architecture/run-observability-and-manual-trigger.md:929-937` |
| S2 | Solução segura e mínima definida | ✓ | §4 (Opção A: manter opt-in + operacionalizar via compose) |
| S3 | UI identifica claramente a acção de iniciar o primeiro Run, se necessário | ✓ | §5.3 (texto accionável em `WebDashboardService.cs:8535-8539`) |
| S4 | Autenticação/autorização intactas | ✓ | §12 (gate 503, gate CSRF, gate bootstrap, machine token — tudo intacto) |
| S5 | Testes relevantes passam | parcial | §6.3 — não foi possível executar `dotnet test` por ausência de runtime neste ambiente; análise estática via sub-agente confirma 4/4 PASS |
| S6 | Sem alterações fora do âmbito | ✓ | §12 |
| S7 | Terreno preparado para `W-FIRST-E2E-TEST` | ✓ | §10 |

## 14. Dívida conhecida (não alterada por esta wave)

- `--web-allow-trigger` continua a ser opt-in (default off) ao nível do binário. Esta wave elimina a fricção operacional (compose passa a flag) mas **não** decide se o default devia mudar. Esse decisão continua pendente e é explicitamente uma das questões em aberto da auditoria (`2026-09-25-w-audit-web-admin-e2e.md:987`).
- O erro 503 mantém o código `web-allow-trigger-disabled` (decisão congelada). O UI agora dá a explicação accionável em vez do erro bruto.
- Login throttle continua in-memory (perde-se com restart) — fora do âmbito.
- Sem OpenAPI/Swagger — fora do âmbito.
- Sem teste E2E HTTP completo — `W-FIRST-E2E-TEST` (próxima wave).
