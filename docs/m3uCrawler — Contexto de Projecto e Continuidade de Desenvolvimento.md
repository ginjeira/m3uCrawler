
# m3uCrawler — Contexto de Projecto, Decisões e Linha de Evolução

Esta mensagem serve para estabelecer contexto na **conversa principal do projecto `m3uCrawler`**.

Existe uma conversa separada de planeamento/brainstorming onde têm sido discutidas ideias, alternativas e decisões arquitecturais. Esta mensagem consolida as conclusões relevantes dessa conversa para que o desenvolvimento na conversa principal possa continuar com continuidade e sem perder contexto.

**Não tratar esta mensagem como uma especificação fechada de implementação.**

Deve ser utilizada como contexto de projecto, princípios arquitecturais, decisões já tomadas e direcção de evolução.

---

# 1. Forma de trabalhar neste projecto

O `m3uCrawler` é um projecto em desenvolvimento activo.

Quando trabalharmos nele:

- consultar primeiro o estado real do repositório;
- não assumir que uma ideia discutida anteriormente já está implementada;
- não inventar componentes, APIs ou modelos que não existam;
- distinguir claramente entre:
  - o que já existe;
  - o que foi decidido;
  - o que foi proposto;
  - o que ainda está em aberto.

O repositório é a source of truth relativamente ao código.

A documentação existente no projecto também deve ser respeitada.

Não reorganizar arquitectura ou documentação sem uma razão concreta.

---

# 2. Princípio de colaboração

Quero que a conversa avance efectivamente o projecto.

Evitar respostas que sejam apenas:

> "Sim, faz sentido."

ou:

> "O próximo passo é..."

Quando estivermos a trabalhar numa decisão técnica, sempre que possível:

- analisar;
- comparar alternativas;
- identificar consequências;
- propor uma solução concreta;
- produzir modelos/API/estrutura/testes quando apropriado;
- e deixar material accionável para o desenvolvimento.

Quando uma decisão ainda não estiver suficientemente amadurecida, não a apresentar como definitiva.

---

# 3. Estado conceptual do m3uCrawler

O projecto começou essencialmente como um crawler capaz de:

```text
descobrir playlists
        ↓
obter M3U/M3U8
        ↓
fazer parsing
        ↓
validar
        ↓
testar streams
        ↓
gerar playlists
```

A direcção que estamos a explorar é evoluir progressivamente o projecto para uma plataforma capaz de compreender **o que cada stream representa**, independentemente da forma como aparece na source.

A visão conceptual é:

```text
Discovery
    ↓
Source
    ↓
Playlist
    ↓
Stream
    ↓
Normalization
    ↓
Canonical Channel
    ↓
ChannelSource
    ↓
Ordering
    ↓
Source Priority
    ↓
Generated Playlist
    ↓
Dispatcharr
```

Isto não significa reescrever o projecto de uma vez.

A evolução deve aproveitar a arquitectura existente.

---

# 4. Separação fundamental de conceitos

Uma das decisões conceptuais mais importantes é separar:

```text
Channel
Stream
Source
ChannelSource
Ordering
Source Priority
Playlist
Group
```

### Channel

Representa a identidade conceptual de um canal.

Exemplo:

```text
canonical_id = pt.rtp1
name         = RTP 1
```

### Stream

É uma ocorrência concreta de um stream.

Exemplos:

```text
RTP1
RTP 1 HD
RTP 1 FHD
RTP 1 UHD
```

podem ser streams diferentes.

### ChannelSource

Representa a relação entre um canal canónico e uma determinada source/stream.

### Ordering

Define **onde o canal aparece**.

### Source Priority

Define **qual das várias fontes disponíveis deve ser preferida**.

Estas duas coisas não devem ser confundidas.

---

# 5. Catálogo Canónico

Estamos a adoptar o conceito de um **Canonical Channel Catalogue**.

Existe já um catálogo inicial português:

```text
m3ucrawler_pt_canonical_catalog.json
```

Este catálogo contém uma baseline de:

- canais;
- canonical IDs;
- aliases;
- posições;
- grupos;
- categorias;
- regras de matching;
- políticas relacionadas com VOD;
- opções de configuração.

O catálogo define, como baseline portuguesa:

```text
1 — RTP 1
2 — RTP 2
3 — SIC
4 — TVI
```

e não deve ser interpretado como uma cópia imutável de uma grelha específica de MEO, NOS ou Vodafone.

As grelhas dos operadores podem mudar.

O catálogo representa uma **normalização/base de referência**.

---

# 6. Identidade canónica ≠ posição

Nunca assumir:

```text
posição 1 = RTP 1
```

porque uma source tem `channel number = 1`.

A posição da source não determina a identidade.

A identidade deve resultar de:

- `tvg-id`;
- IDs conhecidos;
- nome normalizado;
- aliases;
- metadata;
- heurísticas controladas;
- eventualmente fuzzy matching.

O número da source pode ser preservado como informação da source, mas não deve ser utilizado como identidade canónica.

---

# 7. Aliases

Os aliases são fundamentais para resolver diferenças entre playlists.

Exemplo:

```text
RTP1
RTP 1
RTP 1 HD
RTP 1 FHD
RTP1 HD
```

→

```text
pt.rtp1
RTP 1
```

O nome original da source deve continuar preservado.

O catálogo deve permitir manter:

```text
canonical name
aliases
source names
```

sem os confundir.

---

# 8. Matching

O matching deverá evoluir para um componente explícito.

A prioridade conceptual inicial é:

```text
1. exact tvg-id
2. canonical/provider ID conhecido
3. normalized channel name
4. alias
5. logo/name heuristic
```

O matching deve idealmente produzir:

```text
canonical_id
match_method
confidence
```

Exemplo:

```text
pt.rtp1
alias
1.00
```

ou:

```text
pt.sic
normalized-name
0.98
```

A associação automática deve poder depender de um nível mínimo de confiança.

---

# 9. Canais desconhecidos

Um canal que não seja reconhecido não deve ser simplesmente apagado.

Deve poder entrar num estado como:

```text
Unmatched
Pending
Other
```

e ser posteriormente tratado pelo operador.

O Dashboard poderá permitir:

- associar a canal existente;
- criar novo canal;
- adicionar alias;
- alterar categoria;
- ignorar;
- marcar como VOD;
- corrigir o matching.

As decisões manuais devem poder melhorar o catálogo/matching futuro.

---

# 10. Ordering Lists

Uma playlist M3U pode servir como **fonte de uma Ordering List**.

Exemplo:

```text
M3U
 ↓
preservar ordem
 ↓
normalizar nomes
 ↓
resolver canonical channels
 ↓
criar ordering list
```

A ordem original da playlist é informação importante, mas não deve tornar-se uma verdade imutável.

Depois da importação, o operador deve poder alterar a ordem.

---

# 11. Múltiplas Ordering Lists

Não queremos uma lógica hardcoded para:

```text
MEO
NOS
Vodafone
```

Em vez disso, esses casos devem ser representados por listas configuráveis.

Exemplo:

```text
Portugal — Principal
Portugal — MEO
Portugal — NOS
Portugal — Vodafone
Portugal — Minha Lista
Portugal — Desporto
```

Cada uma pode ter:

- canais diferentes;
- posições diferentes;
- regras próprias.

Assim, adicionar uma nova grelha não exige alteração de código.

---

# 12. Dashboard como control plane

Esta é uma regra arquitectural especialmente importante:

> **Se uma decisão puder razoavelmente ser tomada pelo operador do sistema, não deve estar hardcoded no código. Deve existir uma configuração persistente e ser possível geri-la através do Dashboard.**

Isto inclui, entre outras coisas:

- VOD;
- TV;
- rádio;
- grupos;
- categorias;
- aliases;
- normalização;
- prefixes/suffixes;
- matching;
- confidence thresholds;
- fuzzy matching;
- canais desconhecidos;
- canais fora da ordering list;
- duplicados;
- HD/SD/FHD/UHD;
- streams sem EPG;
- streams indisponíveis;
- prioridades de source;
- ordering;
- ordering lists;
- grupos M3U;
- inclusão/exclusão de grupos;
- políticas de importação;
- políticas de retenção;
- políticas de exportação.

Não significa que absolutamente todos os parâmetros tenham de ser expostos.

Significa que decisões operacionais importantes não devem ficar escondidas em `if`s hardcoded.

---

# 13. VOD

VOD é um ponto que deve ser tratado explicitamente.

VOD não é Linear TV.

Nunca deve consumir posições da grelha linear.

Devem existir políticas configuráveis para:

```text
importar VOD
manter VOD
excluir VOD
grupos VOD
```

Idealmente também permitir políticas por source/grupo.

Exemplo conceptual:

```text
Import TV      ON
Import Radio   ON
Import VOD     OFF
```

ou:

```text
Import VOD     ON
Keep VOD       ON
```

O comportamento não deve ser hardcoded.

---

# 14. Rádio

Rádio deve ser tratado separadamente de televisão.

Deve poder existir:

```text
Radio
Rádios | Portugal
Rádios | Internacional
```

com políticas próprias de:

- importação;
- ordenação;
- retenção;
- agrupamento.

---

# 15. Grupos

`group-title` proveniente de uma source não deve ser considerado automaticamente como grupo canónico.

Deve existir uma camada de normalização.

Exemplo:

```text
PORTUGAL SPORTS
Portugal Sports
SPORTS PT
PT SPORT
```

podem eventualmente ser mapeados para:

```text
Portugal | Desporto
```

Mas o mapeamento deve ser configurável.

O Dashboard deve permitir gerir grupos e respectivos mappings.

---

# 16. Qualidade

Variantes:

```text
SD
HD
FHD
UHD
4K
```

devem poder representar streams diferentes do mesmo canal.

A política de preferência deve ser configurável.

Exemplo:

```text
Prefer UHD
Prefer FHD
Prefer HD
Best available
```

Não assumir automaticamente:

```text
HD = sempre melhor
```

porque a qualidade real de um stream também depende de estabilidade, bitrate, latência, EPG, etc.

---

# 17. Duplicados

Duplicação de canais e duplicação de streams devem ser conceitos distintos.

Exemplo:

```text
RTP 1
RTP1
RTP 1 HD
RTP 1 FHD
```

podem corresponder ao mesmo:

```text
Canonical Channel
```

mas continuar a existir como múltiplos streams.

O sistema deve permitir escolher a política:

- manter;
- deduplicar;
- preferir;
- excluir;
- manter para fallback.

---

# 18. EPG

Streams sem EPG não devem ser automaticamente considerados inválidos.

Devem existir políticas configuráveis:

```text
keep
exclude
keep but flag
prefer streams with EPG
```

---

# 19. Streams indisponíveis

O crawler já testa streams.

A evolução deverá distinguir claramente:

```text
discovered
validated
reachable
unreachable
timeout
dead
```

e permitir políticas sobre o que fazer com cada estado.

Por exemplo:

```text
manter no catálogo
manter apenas no histórico
não exportar
eliminar
```

---

# 20. Source Priority

Um canal pode ter múltiplas sources.

Exemplo:

```text
RTP 1

Source A — FHD
Source B — HD
Source C — SD
```

A ordering list determina:

```text
onde RTP 1 aparece
```

A source priority determina:

```text
qual source utilizar primeiro
```

Devem continuar a ser conceitos independentes.

---

# 21. Proveniência

Sempre que possível, preservar a origem dos dados.

É importante conseguir perceber:

```text
de onde veio este stream?
de onde veio este alias?
porque foi feito este matching?
quem criou esta associação?
quando foi criada?
qual a confiança?
```

Isto será particularmente importante quando o operador começar a corrigir informação através do Dashboard.

---

# 22. Catálogo persistente

O JSON inicial deve ser considerado uma **seed**.

A arquitectura final deverá permitir que o catálogo seja persistente e evolua independentemente do ficheiro inicial.

O conceito poderá incluir:

```text
CanonicalId
Name
Country
Category
Subcategory
Aliases
ExternalIds
Logo
Metadata
Provenance
Confidence
Active
CreatedAt
UpdatedAt
```

A implementação concreta deve ser determinada depois de analisar a persistência já existente.

Não criar uma segunda camada de persistência sem necessidade.

---

# 23. Dispatcharr

A arquitectura deverá preparar uma integração futura com Dispatcharr.

Fluxo conceptual:

```text
Discovery
    ↓
Playlist Generation
    ↓
Dispatcharr Sync
    ↓
Matching
    ↓
MatchPlan
    ↓
Validation
    ↓
Apply
    ↓
Source Ordering
```

Mas Dispatcharr não deve tornar-se a source of truth do catálogo.

O m3uCrawler deve manter a sua própria identidade canónica.

---

# 24. O que NÃO fazer

Não introduzir lógica específica do género:

```text
if channel == "RTP 1"
```

para resolver decisões que pertencem ao catálogo.

Não introduzir:

```text
if operator == "MEO"
if operator == "NOS"
if operator == "Vodafone"
```

para representar grelhas.

Não introduzir:

```text
if vod then discard
```

como comportamento fixo.

Não introduzir:

```text
if HD then prefer
```

como regra universal.

Não eliminar automaticamente canais desconhecidos.

Não utilizar o número da source como identidade canónica.

Não assumir que `group-title` é o grupo canónico.

---

# 25. Princípio de evolução

A arquitectura deve evoluir nesta direcção:

```text
Crawler
   ↓
Discovery Platform
   ↓
Normalization Platform
   ↓
Canonical Catalogue
   ↓
Matching
   ↓
Channel/Source Management
   ↓
Ordering
   ↓
Playlist Management
   ↓
Dispatcharr Integration
```

Mas sempre de forma incremental.

Não reescrever componentes estáveis sem necessidade.

---

# 26. Estado do catálogo português

O catálogo português fornecido deve ser considerado o **baseline actual para o trabalho de normalização**.

Contém, entre outros:

- canonical IDs;
- nomes;
- aliases;
- grupos;
- posições;
- categorias;
- regras de matching;
- opções de VOD;
- opções de qualidade.

O próprio catálogo estabelece que:

- a identidade canónica é independente da posição;
- os grupos são canónicos mas configuráveis;
- VOD é separado de Linear TV;
- playlists podem conter aliases, duplicados e numbering específico;
- as decisões configuráveis devem ser expostas ao Dashboard. 

Não assumir, contudo, que o catálogo é perfeito ou definitivo.

---

# 27. A próxima playlist/base list

A playlist anteriormente analisada **não deve ser considerada uma boa referência para a ordenação portuguesa**, porque a sua ordem não corresponde a uma grelha portuguesa coerente.

O princípio português que estamos a adoptar como baseline é:

```text
1 RTP 1
2 RTP 2
3 SIC
4 TVI
```

A próxima playlist que for encontrada poderá servir como uma fonte muito melhor para construir uma Ordering List inicial.

Importante:

> uma playlist utilizada para construir uma Ordering List é uma fonte de dados/configuração, não uma regra hardcoded no software.

---

# 28. Prioridade das decisões

Quando houver conflito entre ideias antigas e a arquitectura actual, seguir esta ordem:

```text
1. Código existente e testes
2. Documentação oficial do projecto
3. Decisões arquitecturais já consolidadas
4. Este contexto
5. Ideias ainda em discussão
```

Não tratar hipóteses discutidas na conversa de planeamento como decisões definitivas sem confirmação.

---

# 29. Forma de conduzir futuras alterações

Para alterações significativas:

### Primeiro
Analisar o estado actual.

### Depois
Explicar o impacto arquitectural.

### Depois
Propor a solução.

### Depois
Definir contratos/modelos/testes.

### Depois
Implementar.

### Finalmente
Executar:

```text
build
tests
review
```

e registar claramente o resultado.

Sempre que uma alteração envolver vários ficheiros relacionados, preferir uma implementação coerente e completa em vez de uma sucessão de pequenos patches desconexos.

---

# 30. Objectivo final

O objectivo não é simplesmente ter um crawler que encontra playlists.

Queremos chegar a um sistema que consiga compreender:

```text
"Este stream concreto
é uma ocorrência de
RTP 1,
é proveniente desta source,
tem estas características,
foi associado com esta confiança,
deve aparecer nesta posição
e esta é a source que deve ser preferida."
```

E queremos que o operador consiga controlar as decisões relevantes através do Dashboard.

A arquitectura deverá, portanto, evoluir para:

```text
                ┌─────────────────┐
                │ Canonical       │
                │ Catalogue       │
                └────────┬────────┘
                         │
                         ▼
Source ──► Playlist ──► Stream ──► Matching
                         │             │
                         │             ▼
                         │       ChannelSource
                         │             │
                         ▼             ▼
                    Validation     Ordering
                                       │
                                       ▼
                                Source Priority
                                       │
                                       ▼
                               Playlist Output
                                       │
                                       ▼
                                  Dispatcharr
```

Esta é a direcção arquitectural de longo prazo.

**Não é necessário implementar tudo de uma vez.**

O trabalho deve ser feito por fases, preservando o que já funciona e utilizando esta visão como orientação para as decisões futuras.