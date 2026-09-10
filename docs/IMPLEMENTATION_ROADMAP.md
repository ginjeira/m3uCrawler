# m3uCrawler — Plano de Implementação e Evolução da Plataforma

## 1. Objectivo

Este documento transforma a visão arquitectural definida em:

> `m3uCrawler — Contexto de Projecto, Decisões e Linha de Evolução`

num plano concreto de implementação.

O objectivo é evoluir o m3uCrawler de um crawler/discovery pipeline para uma plataforma funcional de:

```text
Discovery
    ↓
Sources
    ↓
Playlists
    ↓
Streams
    ↓
Normalization
    ↓
Canonical Catalogue
    ↓
ChannelSource
    ↓
Validation
    ↓
Ordering
    ↓
Source Priority
    ↓
Playlist Generation
    ↓
Dispatcharr
```

O operador deverá conseguir controlar as decisões relevantes através do Dashboard.

O plano deve ser implementado **por fases funcionais**, mas sem transformar o desenvolvimento numa sucessão de análises/revisões sem entrega.

Cada fase deve produzir funcionalidade utilizável.

---

# 2. Princípios de implementação

## 2.1 Source of truth

O repositório e os testes existentes são a referência do estado actual.

Não reimplementar funcionalidades já existentes.

Não criar uma segunda camada para resolver um problema que já tenha uma solução funcional.

---

## 2.2 Evolução incremental

Preservar:

- discovery Telegram;
- parsing M3U;
- Xtream publication resolver;
- validação;
- classification;
- matching;
- catálogo persistente;
- ownership Dispatcharr;
- manutenção da playlist;
- sanitização de credenciais.

A nova arquitectura deve integrar estes componentes.

Não reescrever componentes estáveis sem necessidade.

---

## 2.3 Dashboard como Control Plane

Decisões operacionais relevantes devem ser persistentes e administráveis pelo Dashboard.

O Dashboard não deve ser apenas uma interface de diagnóstico.

Deve tornar-se o **control plane do m3uCrawler**.

O contexto do projecto estabelece explicitamente este princípio. fileciteturn36file7L1-L2

---

## 2.4 Identidade e posição são conceitos independentes

Nunca utilizar:

```text
source channel number
```

como identidade canónica.

A identidade deve resultar de matching.

A posição deve resultar de uma Ordering List.

O contexto estabelece explicitamente esta separação. fileciteturn36file11L1-L2

---

## 2.5 Ordering e Source Priority são independentes

Ordering responde:

```text
Onde aparece o canal?
```

Source Priority responde:

```text
Qual dos streams disponíveis deve ser utilizado?
```

Não misturar os dois mecanismos.

---

## 2.6 Dados desconhecidos não são eliminados

Um canal desconhecido deve poder entrar em:

```text
Unmatched
Pending
Review
Other
```

e ser tratado posteriormente pelo operador.

O Dashboard deve permitir corrigir o matching e melhorar o catálogo. fileciteturn36file6L1-L2

---

# 3. Estado actual

A implementação actual já contém:

- pipeline Telegram;
- descoberta de publicações Xtream;
- parser adaptativo;
- classification;
- matching;
- catálogo persistente SQLite;
- aliases;
- identity rules;
- affinity groups;
- review items;
- ownership de canais/streams Dispatcharr;
- SyncRun;
- Dashboard parcial;
- geração de playlist;
- sincronização Dispatcharr.

O catálogo persistente já possui entidades para canal canónico, aliases, regras, affinity groups, review items, ownership e SyncRun. 

O `CatalogResolver` já fornece operações persistentes sobre estes elementos. 

O Dashboard já expõe parte deste estado, incluindo catálogo e diagnósticos. 

Portanto, as próximas fases devem **completar e integrar** estas capacidades.

---

# 4. Estratégia geral

A implementação será dividida em:

```text
PHASE 0 — Baseline / preparação
PHASE 1 — Canonical Catalogue
PHASE 2 — Dashboard Control Plane
PHASE 3 — Matching & Review
PHASE 4 — Sources & ChannelSource
PHASE 5 — Ordering Lists
PHASE 6 — Source Priority
PHASE 7 — Playlist Generation
PHASE 8 — TV / Radio / VOD / Groups
PHASE 9 — Stream Quality / EPG / Availability
PHASE 10 — Dispatcharr Integration
PHASE 11 — Operations / Automation
PHASE 12 — Production consolidation
```

As fases seguintes podem começar assim que os contratos necessários estejam implementados.

Não efectuar uma nova grande análise do projecto entre cada fase.

---

# 5. PHASE 0 — Baseline

## Objectivo

Fixar a base actual antes de alterações funcionais.

## Trabalho

- preservar testes existentes;
- garantir build Release;
- confirmar migrations;
- confirmar localização da BD;
- confirmar runtime-data;
- confirmar funcionamento do Dashboard;
- confirmar funcionamento do pipeline actual.

## Resultado

Baseline funcional documentada.

## Critério de conclusão

```text
dotnet build
dotnet test
```

sem regressões.

Depois desta fase, iniciar imediatamente a implementação funcional.

---

# 6. PHASE 1 — Canonical Catalogue

## Objectivo

Transformar o catálogo actual numa verdadeira base de dados canónica utilizável pelo sistema.

## 6.1 Canonical Channel

O canal canónico deve representar:

```text
CanonicalId
DisplayName
Country
Category
EditorialGroup
PublicationPolicy
Enabled
CreatedAt
UpdatedAt
```

Preservar o modelo existente sempre que possível.

Não criar uma segunda entidade paralela.

---

## 6.2 Aliases

Permitir:

```text
Canonical Channel
    ├── Alias 1
    ├── Alias 2
    ├── Alias 3
    └── ...
```

Operações:

- listar;
- adicionar;
- editar;
- remover;
- detectar conflitos.

---

## 6.3 Identidades especiais

Manter:

```text
IdentityRule
AffinityGroup
AffinityMember
```

como mecanismos distintos.

---

## 6.4 Grupos editoriais

Os grupos devem deixar de ser apenas enums rígidos sempre que a decisão for editorial/configurável.

Deve existir uma representação persistente dos grupos que permita:

```text
nome
display name
ordem
activo
categoria
```

e posteriormente mappings de source groups.

---

## 6.5 Seed portuguesa

Integrar o catálogo português fornecido como **seed inicial**.

A seed deve ser importável para o catálogo persistente.

Não deve ser tratada como configuração imutável.

**Estado (2026-09-10)**: ✅ **concluído**.

- `docs/catalog/m3ucrawler_pt_canonical_catalog.json` adicionado ao repositório com `catalog_id=pt-canonical-tv`, `version=1.0`, `country=PT`.
- `m3uCrawler/Services/Catalog/CatalogBaselineImporter.cs` (NOVO): importa o JSON para `CanonicalChannelEntity` + `ChannelAliasEntity` via `CatalogBaselineImporter.ImportAsync(context, baseline)`. Idempotente, aditivo, auditável.
- `m3uCrawler.Tests/CatalogBaselineImporterTests.cs` (NOVO): 19 testes cobrindo conversão de `canonical_id`, resolução de grupo/categoria, idempotência, preservação de canais pré-existentes, sanitização do report, integração com o ficheiro baseline real.
- `m3uCrawler/Services/Catalog/ChannelCatalogBootstrapper.cs` (modificado): `TryImportBaselineAsync` chamado automaticamente após `SeedAsync`. Procura o baseline em três localizações canónicas (env var `M3U_BASELINE_PATH`, `CWD/docs/catalog/`, `<binary>/../../../../../docs/catalog/`). Falha na importação é registada em log mas não aborta o arranque.
- **Não** substituiu o `CatalogSeed` programático — coexiste. O baseline é o source primário do seed PT; `CatalogSeed` permanece como fallback.
- **Limitação conhecida** (PHASE 6 — Grupos): os grupos editoriais continuam a ser o enum `CanonicalEditorialGroup` em vez de entidades persistentes. A resolução de categoria editorial a partir do baseline é heurística (palavras-chave em aliases/nome). PHASE 6 dos grupos está planeada para depois.

---

# 7. PHASE 2 — Dashboard Control Plane

Esta é uma das fases prioritárias.

## Objectivo

Transformar o Dashboard actual numa interface administrativa real.

O contexto do projecto estabelece que decisões operacionais como VOD, grupos, aliases, matching, duplicados, qualidade, ordering e prioridades devem ser configuráveis. fileciteturn36file7L1-L2

---

## 7.1 Dashboard principal

Criar uma página inicial com:

```text
Sources
Channels
Streams
Review
Ordering
Policies
Playlists
Dispatcharr
Runs
System
```

Dashboard deve apresentar:

- estado do crawler;
- última execução;
- canais;
- streams;
- unmatched;
- review pending;
- sources;
- playlist actual;
- estado Dispatcharr.

---

# 8. Dashboard — Catalogue

## Página

```text
Channels
```

### Lista

Colunas:

```text
Position*
Canonical ID
Name
Category
Group
Aliases
Sources
Streams
Status
Policy
```

`Position` é derivada da Ordering List seleccionada.

---

## Operações

Permitir:

- criar canal;
- editar canal;
- activar/desactivar;
- alterar nome;
- alterar categoria;
- alterar grupo;
- alterar política;
- adicionar alias;
- remover alias;
- consultar sources;
- consultar streams.

---

# 9. Dashboard — Channel Detail

Ao seleccionar um canal:

```text
RTP 1
```

mostrar:

### Identity

```text
Canonical ID
Display Name
Category
Group
Status
```

### Aliases

```text
RTP1
RTP 1
RTP 1 HD
RTP 1 FHD
...
```

### Sources

```text
Source
Stream
Quality
EPG
Status
Priority
```

### Ordering

```text
Ordering List
Position
```

### Provenance

```text
created
updated
matching information
```

---

# 10. Dashboard — Review

Criar uma área:

```text
Review
```

com:

```text
Unknown
Pending
Ambiguous
Low Confidence
```

Cada item deve mostrar:

```text
Original Name
Normalized Identity
Source
Group
Suggested Channel
Match Method
Confidence
Reason
```

---

## Acções

```text
Associate Existing Channel
Create New Channel
Add Alias
Exclude
Ignore
Mark as VOD
```

Quando uma decisão humana puder melhorar o matching futuro, deve alterar o catálogo correspondente.

O modelo actual de `ReviewItemEntity` já fornece uma base para esta funcionalidade. 

---

# 11. Dashboard — Sources

Criar:

```text
Sources
```

Cada source deve possuir:

```text
SourceId
Name
Type
Origin
Enabled
Priority
Last Discovery
Last Validation
Health
```

Tipos possíveis:

```text
Telegram
M3U
Xtream
HTTP
File
Manual
```

---

## Source Detail

Mostrar:

```text
Source
Playlists
Channels
Streams
Last execution
Health
Priority
```

Nunca apresentar passwords ou URLs contendo credenciais.

---

# 12. PHASE 3 — Matching & Review

## Objectivo

Transformar matching numa operação explicitamente observável e configurável.

Prioridade conceptual:

```text
1 exact tvg-id
2 known canonical/provider ID
3 normalized name
4 alias
5 controlled heuristic
6 fuzzy
7 review
```

O contexto define esta ordem conceptual. fileciteturn36file11L1-L2

---

## Resultado do matching

Cada associação deve produzir:

```text
CanonicalChannelId
MatchMethod
Confidence
```

e preservar:

```text
OriginalName
NormalizedName
Source
```

---

## Confidence

Configurar thresholds:

```text
Auto Match
Review
Reject
```

Os thresholds devem ser persistentes.

Não hardcodear valores quando forem decisões operacionais.

---

# 13. PHASE 4 — Sources & ChannelSource

## Objectivo

Implementar explicitamente:

```text
Channel
    ↕
ChannelSource
    ↕
Stream
    ↕
Source
```

Uma source pode fornecer vários streams para o mesmo canal.

Um canal pode possuir múltiplas sources.

---

## ChannelSource

Deve permitir guardar:

```text
ChannelId
SourceId
StreamId
Quality
EPG availability
Reachability
Confidence
MatchMethod
FirstSeen
LastSeen
Enabled
```

---

## Resultado

O sistema passa a conseguir responder:

```text
Que streams existem para RTP 1?
De que sources vieram?
Qual funciona?
Qual tem EPG?
Qual é FHD?
Qual foi o último teste?
```

---

# 14. PHASE 5 — Ordering Lists

## Objectivo

Implementar Ordering como entidade independente.

O contexto estabelece que uma playlist pode ser utilizada para criar uma Ordering List e que posteriormente essa lista deve poder ser alterada pelo operador. fileciteturn36file6L1-L2

---

## OrderingList

Modelo:

```text
OrderingList
    Id
    Name
    Country
    Description
    Enabled
    CreatedAt
    UpdatedAt
```

---

## OrderingItem

```text
OrderingListId
CanonicalChannelId
Position
Enabled
```

---

## Listas iniciais

Criar suporte para:

```text
Portugal — Principal
Portugal — MEO
Portugal — NOS
Portugal — Vodafone
Portugal — Minha Lista
```

Não criar lógica específica para MEO/NOS/Vodafone.

São simplesmente Ordering Lists.

---

## Dashboard

Permitir:

- criar lista;
- duplicar lista;
- renomear;
- eliminar;
- activar/desactivar;
- adicionar canal;
- remover canal;
- alterar posição;
- drag & drop;
- preencher posições automaticamente;
- importar ordem de uma M3U.

---

# 15. Importação de Ordering List

Fluxo:

```text
M3U
 ↓
parse
 ↓
normalization
 ↓
matching
 ↓
ordering items
 ↓
review unmatched
 ↓
Ordering List
```

A ordem da M3U é preservada durante a importação.

Mas a M3U não passa a ser uma regra hardcoded.

---

# 16. PHASE 6 — Source Priority

## Objectivo

Permitir múltiplas fontes por canal e determinar qual deve ser utilizada.

Exemplo:

```text
RTP 1

1 — Source A — FHD
2 — Source B — HD
3 — Source C — SD
```

---

## Modelo

Permitir:

```text
SourcePriorityPolicy
```

com critérios como:

```text
Manual
Quality
Reliability
EPG
Latency
Response time
Availability
```

---

## Dashboard

Página:

```text
Source Priority
```

Permitir:

- prioridade global;
- prioridade por canal;
- prioridade por source;
- preferências de qualidade;
- fallback.

---

# 17. PHASE 7 — Playlist Generation

## Objectivo

A playlist deixa de ser simplesmente o resultado directo do crawler.

Passa a ser construída a partir de:

```text
Canonical Catalogue
        +
ChannelSource
        +
Ordering List
        +
Source Priority
        +
Policies
```

---

## Fluxo

```text
Catalogue
   ↓
Ordering
   ↓
ChannelSource
   ↓
Source Priority
   ↓
Validation
   ↓
Playlist
```

---

## Resultado

A playlist final deverá ter:

```text
posição canónica
nome canónico
grupo canónico
logo
stream seleccionado
```

sem perder informação de proveniência internamente.

---

# 18. PHASE 8 — TV / Radio / VOD / Groups

## 18.1 TV

Separar explicitamente:

```text
Linear TV
```

---

## 18.2 Rádio

Criar:

```text
Radio
```

com:

```text
Radio Portugal
Radio International
```

e ordering próprio.

O contexto determina que rádio deve ser tratado separadamente de televisão. fileciteturn36file12L1-L2

---

## 18.3 VOD

VOD nunca utiliza posições de Linear TV.

Políticas:

```text
Import VOD
Keep VOD
Exclude VOD
```

Grupos:

```text
VOD Filmes
VOD Séries
VOD Infantil
VOD Documentários
VOD Concertos
```

---

## Dashboard

Criar:

```text
Import Policies
```

com:

```text
TV       ON/OFF
Radio    ON/OFF
VOD      ON/OFF
```

e políticas específicas.

---

# 19. PHASE 8 — Group Management

Criar:

```text
Groups
```

com:

```text
Canonical Groups
Source Groups
Mappings
```

Exemplo:

```text
Source:
PORTUGAL SPORTS

↓ mapping

Canonical:
Portugal | Desporto
```

O `group-title` da source nunca deve ser automaticamente considerado canonical group. fileciteturn36file12L1-L2

---

## Dashboard

Permitir:

- criar grupo;
- editar;
- activar/desactivar;
- associar source groups;
- alterar ordem;
- definir grupo default;
- excluir grupos.

---

# 20. PHASE 9 — Quality / EPG / Availability

## Quality

Detectar:

```text
SD
HD
FHD
UHD
4K
```

como propriedades do stream.

Não transformar qualidade em identidade.

---

## Quality Policy

Permitir:

```text
Prefer UHD
Prefer FHD
Prefer HD
Prefer SD
Best available
```

Mas a política deve poder combinar qualidade com disponibilidade/reliability.

---

## EPG

Estados:

```text
Available
Unavailable
Unknown
```

Políticas:

```text
Keep
Exclude
Keep + flag
Prefer EPG
```

O contexto determina explicitamente que ausência de EPG não torna automaticamente um stream inválido. fileciteturn36file2L1-L2

---

## Availability

Estados:

```text
Discovered
Validated
Reachable
Unreachable
Timeout
Dead
```

Guardar histórico suficiente para permitir reliability.

---

# 21. PHASE 10 — Dispatcharr

A integração Dispatcharr deve consumir o resultado da nova plataforma.

Fluxo:

```text
Canonical Catalogue
        ↓
ChannelSource
        ↓
Ordering
        ↓
Source Priority
        ↓
Playlist
        ↓
MatchPlan
        ↓
Dispatcharr
```

Dispatcharr não passa a ser source of truth.

O contexto estabelece explicitamente esta regra. fileciteturn36file3L1-L2

---

## Dashboard Dispatcharr

Criar:

```text
Dispatcharr
```

com:

```text
Connection
Status
Last Sync
Channels
Streams
Changes
Protected Streams
Crawler Managed
External
Unknown
```

---

## Sync

Permitir:

```text
Dry Run
Preview
Apply
```

e mostrar:

```text
Create
Update
Keep
Remove
Protected
Review
```

A protecção existente de streams externas/desconhecidas deve ser preservada.

---

# 22. PHASE 11 — Operations

## Dashboard

Criar:

```text
Runs
```

com histórico:

```text
Run ID
Start
End
Duration
Sources
Playlists
Streams
Matched
Review
Excluded
Created
Removed
Errors
```

O modelo `SyncRunEntity` existente já fornece uma base para este histórico. 

---

## Run Detail

Mostrar:

```text
Discovery
Parsing
Validation
Matching
Ordering
Playlist
Dispatcharr
```

com resultados.

Nunca expor credenciais.

---

# 23. PHASE 12 — Automation

Depois das capacidades anteriores estarem funcionais:

```text
Scheduled Discovery
Scheduled Validation
Scheduled Playlist Generation
Scheduled Dispatcharr Sync
```

com configuração no Dashboard.

Exemplo:

```text
Discovery
Every 24h

Validation
Every 6h

Dispatcharr
After successful playlist generation
```

---

# 24. Dashboard — Estrutura final

O Dashboard deverá evoluir para:

```text
┌──────────────────────────────────────────┐
│ m3uCrawler                               │
├──────────────────────────────────────────┤
│ Dashboard                                │
│                                          │
│ Sources                                  │
│   ├─ All Sources                         │
│   ├─ Telegram                            │
│   ├─ M3U                                 │
│   └─ Xtream                              │
│                                          │
│ Channels                                 │
│   ├─ Catalogue                           │
│   ├─ Unmatched                           │
│   ├─ Review                              │
│   └─ Aliases                             │
│                                          │
│ Ordering                                 │
│   ├─ Lists                               │
│   └─ Source Priority                     │
│                                          │
│ Groups                                   │
│                                          │
│ Policies                                 │
│   ├─ TV                                  │
│   ├─ Radio                               │
│   ├─ VOD                                 │
│   ├─ Quality                             │
│   ├─ EPG                                 │
│   └─ Availability                        │
│                                          │
│ Playlists                                │
│                                          │
│ Dispatcharr                              │
│                                          │
│ Runs                                     │
│                                          │
│ System                                   │
└──────────────────────────────────────────┘
```

---

# 25. Prioridade de implementação

Para tornar o sistema funcional rapidamente, a prioridade deve ser:

## Prioridade 1

```text
Canonical Catalogue
+
Dashboard Catalogue
+
Review
```

---

## Prioridade 2

```text
ChannelSource
+
Sources
+
Stream state
```

---

## Prioridade 3

```text
Ordering Lists
+
Dashboard Ordering
```

---

## Prioridade 4

```text
Source Priority
+
Playlist Generation
```

---

## Prioridade 5

```text
TV / Radio / VOD
+
Groups
+
Policies
```

---

## Prioridade 6

```text
Quality
EPG
Availability
```

---

## Prioridade 7

```text
Dispatcharr
```

---

## Prioridade 8

```text
Automation
Operations
```

---

# 26. Regras para cada implementação

Cada fase deve seguir:

```text
1. Definir contrato/modelo necessário
2. Implementar persistência
3. Implementar serviço
4. Integrar pipeline
5. Implementar API
6. Implementar Dashboard
7. Criar testes
8. Build
9. Test
10. Commit
```

Não implementar apenas a API deixando o Dashboard para uma fase indefinida.

Quando uma capacidade for destinada ao operador, a API e o Dashboard devem evoluir em conjunto.

---

# 27. Testes

Cada funcionalidade nova deve possuir testes adequados.

Categorias:

```text
Unit
Integration
Persistence
API
Dashboard
Pipeline
Regression
```

Não definir o número esperado de testes como requisito arquitectural.

O número de testes é apenas um indicador operacional.

---

# 28. Compatibilidade

Durante a evolução:

- manter CLI existente;
- manter pipeline Telegram;
- manter formato funcional da playlist;
- manter sanitização;
- manter ownership Dispatcharr;
- manter comportamento de manutenção;
- manter compatibilidade com deployments existentes quando possível.

Alterações incompatíveis devem ser explicitamente documentadas.

---

# 29. Segurança

Nunca guardar ou apresentar:

```text
Xtream passwords
Telegram tokens
API keys
```

em:

```text
logs
Dashboard
reports
review
provenance
```

A playlist funcional continua a ser uma excepção porque necessita das URLs reais para funcionar.

Todos os restantes artefactos devem ser sanitizados.

---

# 30. Critério de conclusão da plataforma

A implementação será considerada funcional quando o seguinte fluxo puder ser executado sem intervenção no código:

```text
Source descoberta
       ↓
Playlist importada
       ↓
Streams encontrados
       ↓
Streams normalizados
       ↓
Canonical Channels associados
       ↓
Unknowns aparecem no Review
       ↓
Operador corrige no Dashboard
       ↓
ChannelSource actualizado
       ↓
Ordering List seleccionada
       ↓
Source Priority aplicada
       ↓
Playlist gerada
       ↓
Dispatcharr sincronizado
```

E o operador conseguir alterar as decisões sem alterar código.

---

# 31. Estado final pretendido

O sistema deve permitir responder:

```text
Qual é este stream?

→ RTP 1

De onde veio?

→ Source X

Qual é a sua qualidade?

→ FHD

Tem EPG?

→ Sim

Está disponível?

→ Sim

Como foi associado?

→ Alias

Com que confiança?

→ 1.00

Em que posição deve aparecer?

→ 1

Qual a source preferida?

→ Source X

Se Source X falhar?

→ Source Y

Está incluído na playlist?

→ Sim

Está sincronizado com Dispatcharr?

→ Sim
```

Tudo isto deve resultar do modelo persistente e das políticas configuradas, e não de regras hardcoded.

---

# 32. Ordem efectiva de execução

A implementação deve avançar nesta ordem:

```text
PHASE 0
Baseline
   ↓
PHASE 1
Canonical Catalogue
   ↓
PHASE 2
Dashboard Control Plane
   ↓
PHASE 3
Matching / Review
   ↓
PHASE 4
Sources / ChannelSource
   ↓
PHASE 5
Ordering Lists
   ↓
PHASE 6
Source Priority
   ↓
PHASE 7
Playlist Generation
   ↓
PHASE 8
TV / Radio / VOD / Groups
   ↓
PHASE 9
Quality / EPG / Availability
   ↓
PHASE 10
Dispatcharr
   ↓
PHASE 11
Operations
   ↓
PHASE 12
Automation
```

As fases devem ser implementadas de forma contínua.

Não iniciar uma nova ronda de arquitectura global a cada fase.

Se surgir um problema local, resolver no contexto da fase sem interromper a evolução geral, excepto quando existir uma incompatibilidade arquitectural real.

---

# 33. Definition of Done

Uma fase só está concluída quando:

- código implementado;
- persistência implementada quando necessária;
- API implementada;
- Dashboard implementado quando aplicável;
- testes implementados;
- build concluído;
- testes concluídos;
- documentação afectada actualizada;
- commit criado.

Depois disso, avançar directamente para a fase seguinte.

---

# 34. Regra fundamental

Este documento é um **plano de execução**, não uma nova proposta arquitectural.

A arquitectura definida no documento:

> `m3uCrawler — Contexto de Projecto, Decisões e Linha de Evolução`

continua a ser a referência conceptual.

O código existente continua a ser a referência de implementação existente.

Este documento define **como completar a evolução até ao sistema funcional pretendido**.