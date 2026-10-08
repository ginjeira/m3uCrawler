using System;
using System.Collections.Generic;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Catálogo persistente de canais canónicos. Substitui o uso
/// editorial de <c>ChannelCategoryLookup</c> como autorização de
/// criação. Ver
/// <c>docs/architecture/channel-catalog-and-ownership.md</c>.
/// </summary>
public sealed class CanonicalChannelEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Identificador estável único, slug-friendly, imutável após a
    /// criação. Exemplo: "benfica-tv", "sport-tv-1". Usado como
    /// chave estável em logs, ficheiros de seed e URLs internas.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Nome editorial a apresentar (em PT-PT). Exemplo:
    /// "Benfica TV", "Sport TV 1". Mutável: alterar o nome nunca
    /// altera <see cref="Key"/> nem quebra afinidades (que
    /// referenciam a Key).
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// País do catálogo canónico (código ISO, e.g. "pt", "es").
    /// <c>null</c> significa global/agnóstico. Não faz parte da
    /// identidade — <see cref="Key"/> continua a identidade estável.
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// Categoria editorial (Live, Desporto, etc.).
    /// </summary>
    public EditorialCategory EditorialCategory { get; set; }

    /// <summary>
    /// FK para o grupo canónico configurável
    /// (<see cref="CanonicalGroupEntity"/>). Identidade de grupo do
    /// canal. <c>null</c> apenas em canais históricos ainda não
    /// migrados.
    /// </summary>
    public long? GroupId { get; set; }

    /// <summary>
    /// Navegação para o grupo canónico configurável (ver
    /// <see cref="GroupId"/>). Não é carregada por omissão.
    /// </summary>
    public CanonicalGroupEntity? Group { get; set; }

    /// <summary>
    /// Política de publicação. Só <see cref="PublicationPolicy.CreateEligible"/>
    /// permite criar canais. As outras três políticas apenas
    /// protegem canais/streams existentes.
    /// </summary>
    public PublicationPolicy PublicationPolicy { get; set; }

    /// <summary>
    /// Quando <c>false</c> o matcher ignora completamente este
    /// canal (mesmo que o alias bata certo). Usado para
    /// descontinuações temporárias sem apagar o histórico.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<ChannelAliasEntity> Aliases { get; set; } = new();
}

/// <summary>
/// Alias normalizado de uma identidade para um canal canónico. A
/// <c>NormalizedAlias</c> deve estar na mesma forma canónica que o
/// matcher produz (lower-case, espaços em vez de hífens, tokens
/// como "PT"/"VIP" removidos via
/// <c>ChannelNormalizer.Normalize</c>). Ver
/// <see cref="m3uCrawler.Services.Matching.ChannelNormalizer"/>.
/// </summary>
public sealed class ChannelAliasEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Forma canónica da identidade (e.g. "btv hevc pt", "benficatv",
    /// "benfica tv"). Único. Comparação case-sensitive ordinal.
    /// </summary>
    public string NormalizedAlias { get; set; } = string.Empty;

    public long CanonicalChannelId { get; set; }
    public CanonicalChannelEntity? CanonicalChannel { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Identidade externa conhecida de um <see cref="CanonicalChannelEntity"/>
/// (e.g. um <c>tvg-id</c> de um provider, um id de canal de um
/// operador). Normativa em <c>docs/Reestructure/32-DOMAIN-SCHEMA.md</c>.
///
/// <para>
/// É <b>evidência</b> de matching no passo 1/2 da ordem de
/// reconhecimento (<c>docs/Reestructure/05-CATALOGUE.md</c>) e nunca
/// cria identidade por si só (DL-002). O par
/// <see cref="Namespace"/>+<see cref="Value"/> é único: o mesmo valor
/// canónico não pode apontar para dois canais no mesmo namespace. A
/// diferença de canais entre namespaces é detectada em resolução e
/// tratada como ambígua (Review), nunca "escolher o primeiro".
/// </para>
///
/// <para>
/// <see cref="Value"/> é sempre a forma canónica produzida por
/// <see cref="ExternalIdentityNormalizer.Normalize"/> (ADR-0002 §5).
/// O valor original não é persistido aqui.
/// </para>
/// </summary>
public sealed class ExternalIdentityEntity
{
    public long Id { get; set; }

    public long CanonicalChannelId { get; set; }
    public CanonicalChannelEntity? CanonicalChannel { get; set; }

    /// <summary>
    /// Identificador do provider (ex.: chave da source). <c>null</c>
    /// quando a identidade não é específica de um provider.
    /// </summary>
    public string? ProviderId { get; set; }

    /// <summary>
    /// Namespace da identidade (ex.: "tvg-id", "provider:meo").
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Valor canónico (ver <see cref="ExternalIdentityNormalizer"/>).
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Proveniência: "baseline", "ingestion", "operator", …
    /// </summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>
    /// Força/confiança da associação (0..1).
    /// </summary>
    public double Confidence { get; set; } = 1.0;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Regra explícita de identidade que NÃO resolve para um canal
/// publicável (e.g. "Sport TV NBA" → review, não criar). Usado
/// para títulos de bundle/canal de PPV/evento que não devem gerar
/// NewChannel.
/// </summary>
public sealed class IdentityRuleEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Identidade canónica (mesma forma que o matcher produz).
    /// </summary>
    public string NormalizedIdentity { get; set; } = string.Empty;

    public RuleDisposition Disposition { get; set; }

    /// <summary>
    /// Razão textual, sanitizada (sem URLs nem credenciais).
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum RuleDisposition
{
    ReviewOnly = 0,
    Excluded = 1,
}

/// <summary>
/// Tipo de grupo de afinidade.
/// <list type="bullet">
///   <item><see cref="Channel"/> — as variantes resolvem para um
///         canal canónico (<see cref="AffinityGroupEntity.CanonicalChannelKey"/>).
///         Cardinalidade: 0..1 por canal.</item>
///   <item><see cref="Country"/> — as variantes são indicadores
///         country-level injetados no <see cref="CountryChannelValidator"/>;
///         não resolvem para um canal.</item>
/// </list>
/// </summary>
public enum AffinityKind
{
    Channel = 0,
    Country = 1,
}

/// <summary>
/// Grupo de afinidade: um nome e múltiplos membros considerados
/// equivalentes (e.g. "tvi24", "tvi 24", "tvi notícias").
///
/// <para>
/// <see cref="Kind"/> discrimina a semântica:
/// </para>
/// <list type="bullet">
///   <item><b>Channel</b> — resolve para o canal canónico
///         identificado por <see cref="CanonicalChannelKey"/>;
///         membros usados pela resolução de identidade.</item>
///   <item><b>Country</b> — membros injetados no
///         <see cref="CountryChannelValidator"/> como aliases
///         country-level; não resolve canal.</item>
/// </list>
///
/// <para>
/// <see cref="CanonicalChannelId"/> mantém-se apenas como coluna
/// de transição (a identidade passou a ser <see cref="CanonicalChannelKey"/>).
/// </para>
/// </summary>
public sealed class AffinityGroupEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Tipo do grupo. Substitui a inferência implícita por
    /// <c>CanonicalChannelId == null</c>.
    /// </summary>
    public AffinityKind Kind { get; set; } = AffinityKind.Channel;

    /// <summary>
    /// Identidade estável do canal canónico (referencia
    /// <see cref="CanonicalChannelEntity.Key"/>). Obrigatório
    /// quando <see cref="Kind"/> é <see cref="AffinityKind.Channel"/>.
    /// </summary>
    public string? CanonicalChannelKey { get; set; }

    /// <summary>
    /// Código ISO do país que este grupo representa (e.g. "pt", "es").
    /// Obrigatório quando <see cref="Kind"/> é
    /// <see cref="AffinityKind.Country"/>.
    /// </summary>
    public string? CountryCode { get; set; }

    /// <summary>
    /// Coluna de transição. A identidade passou a ser
    /// <see cref="CanonicalChannelKey"/>; esta coluna será removida
    /// numa migration posterior após validação completa.
    /// </summary>
    public long? CanonicalChannelId { get; set; }
    public CanonicalChannelEntity? CanonicalChannel { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<AffinityMemberEntity> Members { get; set; } = new();
}

/// <summary>
/// Membro normalizado de um grupo de afinidade. O valor stored é
/// a forma já normalizada pelo <see cref="ChannelNormalizer"/>.
///
/// <para>
/// <see cref="Kind"/> espelha <see cref="AffinityGroupEntity.Kind"/>
/// para permitir um índice único filtrado: a unicidade global de
/// <see cref="NormalizedMember"/> aplica-se apenas a membros de
/// grupos Channel (uma variante não pode resolver para dois
/// canais). Membros Country não estão sujeitos a essa restrição,
/// pelo que a mesma variante pode existir numa Channel affinity e
/// numa Country affinity.
/// </para>
/// </summary>
public sealed class AffinityMemberEntity
{
    public long Id { get; set; }

    public string NormalizedMember { get; set; } = string.Empty;

    public AffinityKind Kind { get; set; } = AffinityKind.Channel;

    public long AffinityGroupId { get; set; }
    public AffinityGroupEntity? AffinityGroup { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Ownership de um canal do Dispatcharr: se o canal foi criado
/// pelo crawler, se é externo, ou se é desconhecido (bootstrap).
/// </summary>
public enum ChannelOwnership
{
    Unknown = 0,
    CrawlerManaged = 1,
    External = 2,
}

/// <summary>
/// Ownership de uma stream individual dentro de um canal. Aplicado
/// a cada stream do Dispatcharr para impedir que streams
/// externas/desconhecidas sejam removidas por ausência na playlist
/// do crawler.
/// </summary>
public enum StreamOwnership
{
    Unknown = 0,
    CrawlerManaged = 1,
    External = 2,
}

public sealed class DispatcharrChannelOwnershipEntity
{
    public long Id { get; set; }

    /// <summary>ID do canal no Dispatcharr. Único.</summary>
    public long DispatcharrChannelId { get; set; }

    public ChannelOwnership Ownership { get; set; } = ChannelOwnership.Unknown;

    public long? CanonicalChannelId { get; set; }
    public CanonicalChannelEntity? CanonicalChannel { get; set; }

    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime LastObservedAtUtc { get; set; }

    /// <summary>
    /// Texto curto, sanitizado. Pode incluir o nome ou o
    /// source group, mas nunca URLs nem credenciais.
    /// </summary>
    public string Evidence { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class DispatcharrStreamOwnershipEntity
{
    public long Id { get; set; }

    public long DispatcharrStreamId { get; set; }
    public long DispatcharrChannelId { get; set; }

    public StreamOwnership Ownership { get; set; } = StreamOwnership.Unknown;

    /// <summary>
    /// ID do SyncRun que criou a stream (quando aplicável).
    /// Null para streams externas/desconhecidas observadas em
    /// bootstrap.
    /// </summary>
    public long? CreatedBySyncRunId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Item para revisão humana. Gerado quando o matcher não
/// consegue decidir automaticamente (ex.: "PT: SPORT TV NBA"). Estados
/// (W5.4, DL-105/DL-119): <c>Open</c> (a aguardar decisão),
/// <c>InReview</c> (tratamento administrativo iniciado),
/// <c>Resolved</c> (resolvido — mapeia o legacy <c>Approved</c>) e
/// <c>Ignored</c> (ignorado — mapeia o legacy <c>Excluded</c>). O fingerprint
/// é determinístico e baseado em
/// <c>(normalizedIdentity, sourceGroup, reasonSignature)</c> para
/// evitar duplicados.
/// </summary>
public sealed class ReviewItemEntity
{
    public long Id { get; set; }

    /// <summary>
    /// SHA-256 hex de
    /// <c>"{normalizedIdentity}|{sourceGroup}|{reasonSignature}"</c>.
    /// Único. 64 chars.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    public string NormalizedIdentity { get; set; } = string.Empty;
    public string SourceGroup { get; set; } = string.Empty;
    public string ReasonSignature { get; set; } = string.Empty;

    public ReviewItemState State { get; set; } = ReviewItemState.Open;

    /// <summary>
    /// Após decisão humana, referência opcional ao canal canónico
    /// aprovado.
    /// </summary>
    public long? ApprovedCanonicalChannelId { get; set; }
    public CanonicalChannelEntity? ApprovedCanonicalChannel { get; set; }

    /// <summary>
    /// Texto opcional, sanitizado (sem URLs nem credenciais).
    /// </summary>
    public string Note { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }

    // W-REVIEW-01 — evidência persistente da ocorrência que originou a
    // Review. Capturada no momento da ingestion para que a aprovação
    // possa localizar o stream sem depender de joins a DiscoveryCandidate
    // (1:N ambíguo) nem de re-ingestion. Todas nullable para retro-
    // compatibilidade com reviews legadas.

    /// <summary>
    /// URL observada (preservada verbatim — pode conter credenciais em
    /// claro quando a source as expõe). A sanitização acontece apenas
    /// na materialização para <c>ChannelSource.StreamUrl</c> (chokepoint
    /// <c>RecordChannelSourceAsync</c>) e na exposição via API/DTO;
    /// esta coluna persiste a URL bruta do ingestion. <c>null</c> para
    /// reviews criadas antes de W-REVIEW-01 ou quando o stream de
    /// origem não tem URL (e.g. caminhos que ainda não chegaram ao
    /// ingestion).
    /// </summary>
    public string? StreamUrl { get; set; }

    /// <summary>
    /// <c>Source.Id</c> resolvido pelo ingestion. <c>null</c> se a
    /// Review não foi criada via ingestion (e.g. legado, ou caminho
    /// classificação sem Source persistido como em
    /// <c>ChannelMatcher.ClassifyStreams</c>).
    /// </summary>
    public long? SourceId { get; set; }

    /// <summary>
    /// Fingerprint canónico do stream (versão <c>"sfp1"</c>). Hex
    /// minúsculo SHA-256 de <c>"sfp1\n" + canonicalUrl</c>. <c>null</c>
    /// se o URL não é http(s) absoluto, ou para reviews legadas.
    /// </summary>
    public string? StreamFingerprint { get; set; }

    /// <summary>
    /// Versão do algoritmo de <see cref="StreamFingerprint"/>. Sempre
    /// <c>"sfp1"</c> quando <see cref="StreamFingerprint"/> é não-nulo.
    /// </summary>
    public string? StreamFingerprintVersion { get; set; }

    /// <summary>
    /// RunId operacional (D-M4-02a) do ingestion. NUNCA
    /// <c>PipelineTrace.RunId</c> (diagnóstico). <c>null</c> quando o
    /// ingestion corre sem RunId (e.g. legacy, testes unitários, ou
    /// caminhos como <c>ChannelMatcher</c> que não recebem RunId).
    /// </summary>
    public string? RunId { get; set; }
}

/// <summary>
/// W5.4 — Estados do lifecycle de <see cref="ReviewItemEntity"/>
/// (<c>33-STATE-MACHINES.md §ReviewItem</c>, DL-105/DL-119).
///
/// <para>
/// Os valores persistidos preservam os dados legados: <c>Resolved</c> ocupa o
/// valor do antigo <c>Approved</c> (1) e <c>Ignored</c> o do antigo
/// <c>Excluded</c> (2); <c>InReview</c> é um valor novo (3). A coluna é
/// persistida como <c>int</c>, pelo que o mapeamento não exige migration de
/// dados.
/// </para>
/// </summary>
public enum ReviewItemState
{
    Open = 0,
    Resolved = 1,
    Ignored = 2,
    InReview = 3,
}

/// <summary>
/// Registo de uma execução de sincronização Telegram. Sem URLs,
/// credenciais ou tokens — apenas contadores agregados.
/// </summary>
public sealed class SyncRunEntity
{
    public long Id { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }

    /// <summary>Versão da aplicação (commit SHA curto).</summary>
    public string AppVersion { get; set; } = string.Empty;

    // Contadores separados (brief ponto 6 do contract).
    public int CountCreatedCrawlerManaged { get; set; }
    public int CountMergedIntoExternal { get; set; }
    public int CountProtectedExternalStreams { get; set; }
    public int CountRemovedCrawlerManagedStreams { get; set; }
    public int CountReviewRequired { get; set; }
    public int CountExcluded { get; set; }

    /// <summary>
    /// "ok", "cancelled", "error: …". Texto sanitizado.
    /// </summary>
    public string Result { get; set; } = string.Empty;
}

/// <summary>
/// PHASE 11 — Passo detalhado dentro de uma <see cref="SyncRunEntity"/>.
/// Permite desagregar a timeline da execução em fases observáveis
/// (matching, validation, sync, etc.) com tempos e contadores.
/// </summary>
public sealed class SyncRunStepEntity
{
    public long Id { get; set; }

    public long SyncRunId { get; set; }
    public SyncRunEntity? SyncRun { get; set; }

    /// <summary>Nome lógico do passo (e.g. "discovery", "matching", "apply").</summary>
    public string Step { get; set; } = string.Empty;

    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public long DurationMs { get; set; }

    public int ItemsProcessed { get; set; }
    public int ItemsSucceeded { get; set; }
    public int ItemsFailed { get; set; }

    public string Result { get; set; } = "ok";
}

/// <summary>
/// PHASE 9C.4 — Estado terminal de uma <see cref="LiveRunEntity"/>.
/// </summary>
public enum LiveRunTerminalStatus
{
    Unknown = 0,
    Completed = 1,
    Failed = 2,
}

/// <summary>
/// PHASE 9C.4 — Fases da pipeline operacional de uma execução Telegram
/// (Live Run Monitor). São fases sequenciais, não cumulativas: cada
/// execução atravessa exactamente uma vez cada fase (com excepção das
/// fases opcionais, marcadas <c>Optional</c>). Subwave 1 define o
/// modelo persistente; a transição efectiva entre fases será
/// instrumentada na subwave 3.
/// </summary>
public enum LiveRunPhase
{
    /// <summary>Sem fase activa (estado inicial).</summary>
    Idle = 0,

    /// <summary>Leitura de mensagens no Telegram.</summary>
    ReadingTelegram = 1,

    /// <summary>Detecção de candidatos a playlist.</summary>
    Discovering = 2,

    /// <summary>Download de playlists (URL ou anexo).</summary>
    Downloading = 3,

    /// <summary>Análise do conteúdo (parsing M3U).</summary>
    Analyzing = 4,

    /// <summary>Validação por país e por stream.</summary>
    Validating = 5,

    /// <summary>Composição da playlist final (modo maintain).</summary>
    Composing = 6,

    /// <summary>Sincronização com Dispatcharr (opcional, gated por dispatcharr_enabled).</summary>
    SyncingDispatcharr = 7,

    /// <summary>Execução terminada sem erro.</summary>
    Completed = 8,

    /// <summary>Execução terminada com erro.</summary>
    Error = 9,
}

/// <summary>
/// PHASE 9C.4 — Registo persistente de uma execução operacional
/// (Live Run) do ciclo Telegram. Distinta de
/// <see cref="SyncRunEntity"/>, que serve exclusivamente o caminho
/// Dispatcharr. Ambos coexistem por granularidade semântica diferente;
/// não há fusão física das tabelas nem reutilização dos modelos.
///
/// <para>
/// O modelo é a fonte de verdade terminal: o estado in-memory do
/// RunCoordinator é hidratado a partir desta tabela no arranque. Runs
/// sem <see cref="FinishedAtUtc"/> após restart são reportados como
/// <see cref="LiveRunTerminalStatus.Failed"/> (regra da subwave 1,
/// sem estado "unknown" distinto).
/// </para>
/// </summary>
public sealed class LiveRunEntity
{
    public long Id { get; set; }

    /// <summary>GUID estável do run (correlaciona logs e endpoints).</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>
    /// Modo da execução. Espera-se <c>"telegram"</c> ou
    /// <c>"telegram-maintain"</c>. MaxLength 32 para acomodar
    /// variantes futuras sem migration.
    /// </summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Origem da execução (e.g. <c>"cli"</c>, <c>"scheduler"</c>, <c>"manual"</c>).</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// <c>null</c> enquanto a execução está activa. Após
    /// conclusão (normal ou erro) é preenchido pelo RunCoordinator.
    /// </summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>
    /// Estado terminal. <see cref="LiveRunTerminalStatus.Unknown"/>
    /// enquanto a execução decorre; <see cref="LiveRunTerminalStatus.Completed"/>
    /// ou <see cref="LiveRunTerminalStatus.Failed"/> no fecho.
    /// </summary>
    public LiveRunTerminalStatus TerminalStatus { get; set; } = LiveRunTerminalStatus.Unknown;

    /// <summary>
    /// Última mensagem sanitizada registada pelo RunCoordinator.
    /// MaxLength 200 (mesmo limite do feed de actividades).
    /// </summary>
    public string? LastMessage { get; set; }

    /// <summary>
    /// Totalizadores persistidos (JSON serializado, schema livre mas
    /// estável para a duração desta wave). MaxLength 4000 mantém o
    /// payload compacto sem truncar cenários típicos.
    /// </summary>
    public string CountsJson { get; set; } = "{}";

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<LiveRunStepEntity> Steps { get; set; } = new();
}

/// <summary>
/// PHASE 9C.4 — Passo detalhado dentro de uma <see cref="LiveRunEntity"/>.
/// Cada execução regista um <c>LiveRunStepEntity</c> por transição de
/// fase observada (PhaseStarted/PhaseFinished). A unicidade
/// (LiveRunId, PhaseIndex) garante idempotência em retries de
/// instrumentação.
/// </summary>
public sealed class LiveRunStepEntity
{
    public long Id { get; set; }

    public long LiveRunId { get; set; }
    public LiveRunEntity? LiveRun { get; set; }

    /// <summary>Fase da pipeline operacional que este passo representa.</summary>
    public LiveRunPhase Phase { get; set; }

    /// <summary>
    /// Índice sequencial da fase dentro do run (0..N). Usado
    /// para unicidade e ordenação; coincide com a ordem de
    /// declaração da enumeração <see cref="LiveRunPhase"/>.
    /// </summary>
    public int PhaseIndex { get; set; }

    public DateTime PhaseStartedAtUtc { get; set; }
    public DateTime? PhaseFinishedAtUtc { get; set; }

    /// <summary>
    /// Mensagem sanitizada associada a este passo (e.g.
    /// "validated 38/142 streams"). MaxLength 200, mesmo limite
    /// que <see cref="LiveRunEntity.LastMessage"/>.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>Resultado textual curto do passo (<c>"ok"</c>, <c>"error:..."</c>).</summary>
    public string Result { get; set; } = "ok";
}

/// <summary>
/// PHASE 12 — Job agendado. O operador define um nome, uma
/// expressão cron (formato simplificado: <c>minuto hora dia-do-mês
/// mês dia-da-semana</c>) e uma acção opaca por nome. O
/// <c>ScheduledJobRunner</c> calcula o próximo tick e dispara
/// a acção quando chegar a hora.
/// </summary>
public sealed class ScheduledJobEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Cron simplificado (5 campos separados por espaços).</summary>
    public string CronExpression { get; set; } = string.Empty;

    /// <summary>Nome lógico da acção (e.g. "discoverTelegram").</summary>
    public string ActionName { get; set; } = string.Empty;

    /// <summary>
    /// DC-9 — overrides de discovery do job em JSON (camelCase), aplicados
    /// sobre a configuração global. <c>null</c> = sem overrides (herda a
    /// global). Ver <c>m3uCrawler.Services.Configuration.DiscoveryOverrides</c>.
    /// </summary>
    public string? DiscoveryJson { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime? LastRunAtUtc { get; set; }
    public DateTime? NextRunAtUtc { get; set; }
    public string LastResult { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Canal que gerou dúvida no country-level targeting e aguarda
/// decisão humana. Criado quando um stream tem indicadores de país
/// (e.g. "PT" no título) mas não bate num canal canónico
/// conhecido, nem num grupo de afinidade. O utilizador pode:
/// - <c>Aprovar</c>: cria uma IdentityRule com CreateEligible
///   e, opcionalmente, adiciona o membro ao grupo de afinidade
///   do país em questão.
/// - <c>Reprovar</c>: cria uma IdentityRule com Excluded,
///   impedindo que este canal seja aceite no futuro.
/// </summary>
public sealed class PendingCountryApprovalEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Identidade normalizada do canal (e.g. "rtp africa").
    /// </summary>
    public string NormalizedIdentity { get; set; } = string.Empty;

    /// <summary>
    /// Título original do stream (sem normalização).
    /// </summary>
    public string OriginalTitle { get; set; } = string.Empty;

    /// <summary>
    /// Código ISO do país que originou a dúvida (e.g. "pt").
    /// </summary>
    public string CountryCode { get; set; } = string.Empty;

    /// <summary>
    /// URL do stream que gerou a dúvida (para referência).
    /// Sanitizada antes de guardar.
    /// </summary>
    public string StreamUrl { get; set; } = string.Empty;

    /// <summary>
    /// Source group do stream (e.g. "Portugal", "Desporto").
    /// </summary>
    public string? SourceGroup { get; set; }

    /// <summary>
    /// Como é que o canal gerou dúvida:
    /// - "weak_country_match" = tinha indicadores de país mas não bateu em nada conhecido
    /// - "affinity_no_channel" = bateu num grupo de afinidade mas sem canal canónico
    /// </summary>
    public string ReasonSignature { get; set; } = string.Empty;

    public PendingApprovalState State { get; set; } = PendingApprovalState.Open;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
}

public enum PendingApprovalState
{
    Open = 0,
    Approved = 1,
    Rejected = 2,
}

/// <summary>
/// W1 (2026-09-19) — ecossistema técnico de origem. Normativo em
/// <c>docs/Reestructure/32-DOMAIN-SCHEMA.md</c> (Provider) e
/// <c>07-SOURCES.md §1</c>. A <see cref="Key"/> é o namespace que
/// entra na composição de <c>ProviderAccount.AccountKey</c>.
/// </summary>
public sealed class ProviderEntity
{
    public long Id { get; set; }

    /// <summary>
    /// Namespace estável do provider (ex.: "xtream", "telegram").
    /// Único. Entra na composição de <c>AccountKey</c>.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ProviderType Type { get; set; } = ProviderType.Unknown;

    /// <summary>
    /// Capacidades do provider (JSON). Sem segredos.
    /// </summary>
    public string Capabilities { get; set; } = "{}";

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<ProviderAccountEntity> Accounts { get; set; } = new();
}

public enum ProviderType
{
    Unknown = 0,
    Xtream = 1,
    M3u = 2,
    Telegram = 3,
    Http = 4,
    File = 5,
    Manual = 6,
}

/// <summary>
/// W1 (2026-09-19) — conta concreta dentro de um <see cref="ProviderEntity"/>.
///
/// <para>
/// <see cref="AccountKey"/> é a identidade funcional canónica
/// (<c>Provider namespace + external functional identity</c>) e é
/// distinta do identificador técnico <see cref="Id"/> (<c>ProviderAccountId</c>).
/// Único por <c>(ProviderId, AccountKey)</c>.
/// </para>
/// </summary>
public sealed class ProviderAccountEntity
{
    public long Id { get; set; }

    public long ProviderId { get; set; }
    public ProviderEntity? Provider { get; set; }

    /// <summary>
    /// Identidade funcional canónica: namespace do provider + identidade
    /// externa funcional normalizada (NFKC + trim). Não é um id técnico.
    /// </summary>
    public string AccountKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public ProviderAccountStatus Status { get; set; } = ProviderAccountStatus.Discovered;

    /// <summary>
    /// Referência a credenciais (secret store). Nunca contém a credencial
    /// em claro (<c>docs/Reestructure/32-DOMAIN-SCHEMA.md</c>).
    /// </summary>
    public string? CredentialsReference { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum ProviderAccountStatus
{
    Unknown = 0,
    Discovered = 1,
    Active = 2,
    Disabled = 3,
    Error = 4,
}

/// <summary>
/// W1 (2026-09-19) — ocorrência de descoberta associada a um Run.
///
/// <para>
/// Não é uma entidade viva persistente entre execuções: o histórico
/// obtém-se através do <c>Run</c>/<c>Observation</c>. Não mantém
/// <c>FirstSeen</c>/<c>LastSeen</c> (<c>32-DOMAIN-SCHEMA.md</c> e A5).
/// </para>
///
/// <para>
/// A deduplicação por identidade funcional é garantida por
/// <c>(RunId, ProviderAccountId)</c>: duas ocorrências do mesmo Run
/// para a mesma conta funcional são a mesma unidade e não originam
/// processamento equivalente duplicado. Quando não há identidade
/// funcional estável, <see cref="ProviderAccountId"/> é <c>null</c> e
/// a ocorrência é preservada sem dedup (comportamento conservador).
/// </para>
/// </summary>
public sealed class DiscoveryCandidateEntity
{
    public long Id { get; set; }

    public long? ProviderId { get; set; }
    public ProviderEntity? Provider { get; set; }

    /// <summary>
    /// Conta funcional associada. <c>null</c> quando a evidência não
    /// permite derivar identidade estável.
    /// </summary>
    public long? ProviderAccountId { get; set; }
    public ProviderAccountEntity? ProviderAccount { get; set; }

    /// <summary>
    /// Evidência externa observada (ex.: token funcional do provider).
    /// Sanitizada — nunca contém password.
    /// </summary>
    public string ExternalIdentity { get; set; } = string.Empty;

    /// <summary>
    /// Identidade externa funcional normalizada (NFKC + trim).
    /// </summary>
    public string NormalizedIdentity { get; set; } = string.Empty;

    /// <summary>Evidência sanitizada da descoberta (sem credenciais).</summary>
    public string Evidence { get; set; } = string.Empty;

    public DiscoveryCandidateStatus Status { get; set; } = DiscoveryCandidateStatus.Discovered;

    /// <summary>
    /// Identificador opaco do Run a que esta ocorrência pertence.
    /// </summary>
    public string? RunId { get; set; }

    /// <summary>
    /// Source resultante/associada, quando a passagem Candidate→Source
    /// foi explicitada.
    /// </summary>
    public long? SourceId { get; set; }
    public SourceEntity? Source { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// Estados da ocorrência de descoberta (<c>docs/Reestructure/33-STATE-MACHINES.md</c>):
/// <c>Discovered → Normalized → Deduplicated → Accepted | Rejected | Expired</c>.
/// </summary>
public enum DiscoveryCandidateStatus
{
    Discovered = 0,
    Normalized = 1,
    Deduplicated = 2,
    Accepted = 3,
    Rejected = 4,
    Expired = 5,
}

/// <summary>
/// PHASE 4 — Source externa que alimenta o catálogo.
/// Pode ser Telegram, M3U, Xtream, HTTP, ficheiro local, ou manual.
/// Cada source tem 0..N <see cref="ChannelSourceEntity"/>.
/// </summary>
public sealed class SourceEntity
{
    public long Id { get; set; }

    /// <summary>Nome humano (ex.: "Telegram — canal m3u8-pt").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Slug único (ex.: "telegram-m3u8-pt").</summary>
    public string Key { get; set; } = string.Empty;

    public SourceKind Kind { get; set; } = SourceKind.M3U;

    /// <summary>Origem concreta (URL, path, chat_id, etc.). Sanitizada.</summary>
    public string Origin { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public int Priority { get; set; }

    /// <summary>
    /// W1 (2026-09-19) — conta funcional (ProviderAccount) a que esta
    /// Source pertence. <c>null</c> quando a origem não expõe uma
    /// identidade funcional estável. Uma conta pode suportar várias
    /// Sources (<c>docs/Reestructure/07-SOURCES.md §2</c>).
    /// </summary>
    public long? ProviderAccountId { get; set; }
    public ProviderAccountEntity? ProviderAccount { get; set; }

    public DateTime? LastDiscoveryAtUtc { get; set; }
    public DateTime? LastValidationAtUtc { get; set; }

    /// <summary>
    /// W2 (2026-09-19) — última falha de aquisição persistente desta Source
    /// (<c>docs/Reestructure/19-FAILURE-MODEL.md §6</c>). Só é escrita após
    /// retry técnico esgotado ou falha terminal. Representa exactamente a
    /// mesma falha agregada na Run.
    /// </summary>
    public string? LastAcquisitionFailureKind { get; set; }
    public DateTime? LastAcquisitionFailureAtUtc { get; set; }
    public int? LastAcquisitionHttpStatus { get; set; }

    /// <summary>Detalhe sanitizado; nunca contém credenciais/tokens/URLs com credenciais.</summary>
    public string? LastAcquisitionFailureDetail { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<ChannelSourceEntity> ChannelSources { get; set; } = new();
}

public enum SourceKind
{
    Telegram = 0,
    M3U = 1,
    Xtream = 2,
    Http = 3,
    File = 4,
    Manual = 5,
}

/// <summary>
/// PHASE 4 — Associação entre um <see cref="CanonicalChannelEntity"/> e um
/// stream concreto descoberto a partir de uma <see cref="SourceEntity"/>.
/// Representa a proveniência: "este canal pode ser entregue por esta stream
/// desta source". Uma source pode contribuir vários streams para o mesmo
/// canal; um canal pode ter streams vindos de várias sources.
/// </summary>
public sealed class ChannelSourceEntity
{
    public long Id { get; set; }

    public long CanonicalChannelId { get; set; }
    public CanonicalChannelEntity? CanonicalChannel { get; set; }

    public long SourceId { get; set; }
    public SourceEntity? Source { get; set; }

    /// <summary>URL sanitizada do stream (sem credenciais em claro).</summary>
    public string StreamUrl { get; set; } = string.Empty;

    /// <summary>Identificador opaco do stream dentro da source.</summary>
    public string? ExternalStreamId { get; set; }

    /// <summary>
    /// W4 (2026-09-19) — fingerprint canónico do URL do stream
    /// (<c>docs/Reestructure/04-PLAYLIST-STREAM.md §4</c>): hex minúsculo
    /// SHA-256 de <c>version + "\n" + canonicalUrl</c>. Só o hash e a versão
    /// são persistidos; o URL canónico nunca é guardado. <c>null</c> para
    /// rows legacy ou quando o URL não é <c>http</c>/<c>https</c>
    /// fingerprintável.
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>
    /// W4 (2026-09-19) — versão do algoritmo de fingerprint (DL-108;
    /// actualmente <c>"sfp1"</c>). <c>null</c> quando <see cref="Fingerprint"/>
    /// é <c>null</c>.
    /// </summary>
    public string? FingerprintVersion { get; set; }

    public StreamQuality Quality { get; set; } = StreamQuality.Unknown;
    public EpgState Epg { get; set; } = EpgState.Unknown;
    public AvailabilityState Availability { get; set; } = AvailabilityState.Discovered;

    public double? MatchConfidence { get; set; }
    public string MatchMethod { get; set; } = string.Empty;

    /// <summary>
    /// W5.6 (OD-D) — versão da semântica de matching que produziu
    /// <see cref="MatchMethod"/>/<see cref="MatchConfidence"/>
    /// (actualmente <c>"msm1"</c>). Distinta de
    /// <see cref="FingerprintVersion"/> (DL-108) e de
    /// <c>RecognitionPolicy.Version</c>. <c>null</c> em rows legacy.
    /// </summary>
    public string? MatchSemanticsVersion { get; set; }

    public DateTime FirstSeenAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime LastTestedAtUtc { get; set; }
    public long LastResponseTimeMs { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum StreamQuality
{
    Unknown = 0,
    SD = 1,
    HD = 2,
    FHD = 3,
    UHD = 4,
    FourK = 5,
}

public enum EpgState
{
    Unknown = 0,
    Available = 1,
    Unavailable = 2,
}

public enum AvailabilityState
{
    Discovered = 0,
    Validated = 1,
    Reachable = 2,
    Unreachable = 3,
    Timeout = 4,
    Dead = 5,
}

/// <summary>
/// PHASE 5 — Lista de ordenação. Determina a ordem dos canais
/// numa playlist gerada. Existe separadamente do catálogo canónico
/// porque (a) o operador pode ter várias listas ("Portugal Principal",
/// "Minha Lista"), (b) a posição é propriedade da lista, não do
/// canal, (c) duas listas podem incluir o mesmo canal em posições
/// diferentes.
/// </summary>
public sealed class OrderingListEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Slug único, imutável (e.g. "pt-principal").</summary>
    public string Key { get; set; } = string.Empty;

    public string? Country { get; set; }
    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<OrderingItemEntity> Items { get; set; } = new();
}

/// <summary>
/// Item de uma <see cref="OrderingListEntity"/>: referencia um
/// <see cref="CanonicalChannelEntity"/> numa posição específica.
/// A posição 0..N é contínua dentro de cada lista; gaps não são
/// permitidos (ver <c>OrderingListService</c>).
/// </summary>
public sealed class OrderingItemEntity
{
    public long Id { get; set; }

    public long OrderingListId { get; set; }
    public OrderingListEntity? OrderingList { get; set; }

    public long CanonicalChannelId { get; set; }
    public CanonicalChannelEntity? CanonicalChannel { get; set; }

    public int Position { get; set; }
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// PHASE 6 — Política de prioridade entre <see cref="ChannelSourceEntity"/>
/// quando várias streams estão disponíveis para o mesmo
/// <see cref="CanonicalChannelEntity"/>. Persistida como JSON simples
/// (os critérios são extensíveis) e aplicada em playlist generation.
/// </summary>
/// <summary>
/// PHASE 8 — Política de import por tipo de media.
/// Configurável pelo operador: TV ON/OFF, Radio ON/OFF, VOD ON/OFF,
/// mais as políticas específicas de VOD (Import / Keep / Exclude)
/// e grupos-alvo.
/// </summary>
public sealed class ImportPolicyEntity
{
    public long Id { get; set; }

    public MediaKind MediaKind { get; set; } = MediaKind.Live;

    /// <summary>Política específica para VOD (Live/Radio ignoram).</summary>
    public VodPolicy VodPolicy { get; set; } = VodPolicy.ExcludeVod;

    /// <summary>Lista de slugs de grupos alvo separados por vírgula.</summary>
    public string TargetGroupsCsv { get; set; } = string.Empty;

    /// <summary>Lista de slugs de grupos a excluir.</summary>
    public string ExcludedGroupsCsv { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum MediaKind
{
    Live = 0,
    Radio = 1,
    Vod = 2,
}

public enum VodPolicy
{
    ImportVod = 0,
    KeepVod = 1,
    ExcludeVod = 2,
}

/// <summary>
/// PHASE 8 — Grupo canónico persistente. Substitui o antigo enum
/// rígido <c>CanonicalEditorialGroup</c> (removido na Wave D2) por
/// uma entidade configurável pelo operador, identificada pela sua
/// <see cref="Key"/>.
/// </summary>
public sealed class CanonicalGroupEntity
{
    public long Id { get; set; }

    /// <summary>Slug único (e.g. "portugal-live", "portugal-desporto").</summary>
    public string Key { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public string? Country { get; set; }
    public int Order { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// PHASE 3 — Auditoria de cada chamada a
/// <c>CatalogResolver.ResolveAsync</c>. Permite observabilidade
/// fina de matching por canal (que identidades foram resolvidas,
/// qual o caminho, com que confiança).
/// </summary>
public sealed class MatchingAuditEntity
{
    public long Id { get; set; }

    /// <summary>Identidade normalizada (mesma string passada a ResolveAsync).</summary>
    public string NormalizedIdentity { get; set; } = string.Empty;

    /// <summary>Título original (não normalizado) — referência.</summary>
    public string OriginalTitle { get; set; } = string.Empty;

    /// <summary>Source group do stream (e.g. "Portugal", "Desporto").</summary>
    public string? SourceGroup { get; set; }

    /// <summary>Caminho da decisão: <c>Rule</c>, <c>Alias</c>, <c>Unknown</c>, <c>CachedChannel</c>.</summary>
    public string ResolutionKind { get; set; } = "Unknown";

    public long? CanonicalChannelId { get; set; }

    /// <summary>Confiança atribuída (1.0 para canonical/alias; 0 para unknown; ≤1 para rule).</summary>
    public double Confidence { get; set; }

    public string ReasonSignature { get; set; } = string.Empty;

    public DateTime AtUtc { get; set; }
}

/// <summary>
/// PHASE 9 b — Histórico de observações (Quality/EPG/Availability/ResponseTimeMs)
/// por <see cref="ChannelSourceEntity"/>. Permite ver a evolução
/// de um stream ao longo do tempo.
/// </summary>
public sealed class ChannelSourceObservationEntity
{
    public long Id { get; set; }

    public long ChannelSourceId { get; set; }

    public StreamQuality Quality { get; set; }
    public EpgState Epg { get; set; }
    public AvailabilityState Availability { get; set; }
    public long ResponseTimeMs { get; set; }

    public DateTime ObservedAtUtc { get; set; }
}

/// <summary>
/// PHASE 13 (Wave 13-4) — Política de selecção de fontes persistida no
/// catálogo. Contém apenas valores inteiros/booleanos — nunca URLs nem
/// credenciais. <see cref="ScopeKey"/> distingue a política global
/// (<c>"global"</c>) de overrides por canal (<c>"channel:{key}"</c>,
/// reservados para 13-4b). Não tem FK: o âmbito por canal é resolvido
/// pela chave canónica.
/// </summary>
public sealed class SourceSelectionPolicyEntity
{
    public long Id { get; set; }

    /// <summary>"global" para a política por defeito do sistema; "channel:{key}" para override por canal.</summary>
    public string ScopeKey { get; set; } = "global";

    /// <summary>Chave do canal canónico para overrides por canal; <c>null</c> para a política global.</summary>
    public string? CanonicalChannelKey { get; set; }

    /// <summary>Número máximo de fontes por canal. <c>0</c> = não publicar nenhuma; negativo é inválido.</summary>
    public int MaxSourcesPerChannel { get; set; }

    /// <summary>Se true, favorece um representante por fornecedor distinto.</summary>
    public bool PreferDistinctProviders { get; set; }

    /// <summary>Limite opcional por fornecedor. <c>null</c> = sem limite.</summary>
    public int? MaxSourcesPerProvider { get; set; }

    /// <summary>Se true, a Fase B pode preencher lugares restantes com fornecedores já representados.</summary>
    public bool AllowFallbackToSameProvider { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class SourcePriorityPolicyEntity
{
    public long Id { get; set; }

    /// <summary>"global" para a política por defeito do sistema; "channel:{id}" para override por canal.</summary>
    public string Scope { get; set; } = "global";

    public long? CanonicalChannelId { get; set; }

    /// <summary>Critérios em ordem de prioridade (Manual, Quality, Reliability, EPG, Latency, ResponseTime, Availability).</summary>
    public string CriteriaJson { get; set; } = "[\"Manual\",\"Quality\",\"Reliability\",\"Availability\"]";

    /// <summary>Quality preferida (UHD&gt;FHD&gt;HD&gt;SD). Empty = sem preferência.</summary>
    public string PreferredQuality { get; set; } = string.Empty;

    /// <summary>Se true, usa fallback para streams menos preferidas se a escolhida falhar.</summary>
    public bool AllowFallback { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// W5.1 — Política de reconhecimento persistida no catálogo. Scopes: <c>system</c>,
/// <c>global</c>, <c>group:{key}</c>, <c>channel:{key}</c>
/// (<c>docs/Reestructure/38-POLICIES.md §5.1</c>). O fuzzy é <b>opt-in</b>
/// (<see cref="FuzzyEnabled"/> = <c>false</c> por defeito); threshold, margem e
/// pesos são <c>PARAMETER_GAP</c> (<c>null</c> = não decidido). Não contém
/// segredos.
/// </summary>
public sealed class RecognitionPolicyEntity
{
    public long Id { get; set; }

    /// <summary><c>system</c>, <c>global</c>, <c>group:{key}</c> ou <c>channel:{key}</c>.</summary>
    public string ScopeKey { get; set; } = "global";

    /// <summary>Chave canónica para overrides de canal; <c>null</c> nos restantes scopes.</summary>
    public string? CanonicalChannelKey { get; set; }

    /// <summary>Chave de grupo para overrides de grupo; <c>null</c> nos restantes scopes.</summary>
    public string? GroupKey { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Fuzzy opt-in. Default normativo: <c>false</c>.</summary>
    public bool FuzzyEnabled { get; set; }

    /// <summary>PARAMETER_GAP — <c>null</c> significa não decidido.</summary>
    public int? FuzzyThreshold { get; set; }

    /// <summary>PARAMETER_GAP — <c>null</c> significa não decidido.</summary>
    public int? FuzzyAmbiguityMargin { get; set; }

    /// <summary>PARAMETER_GAP — JSON opaco de pesos; <c>null</c> = não decidido.</summary>
    public string? FuzzyWeightsJson { get; set; }

    /// <summary>Versão da policy; incrementa em cada alteração (DL-110).</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// W5.1 — Snapshot imutável da política de reconhecimento efectiva de um Run
/// (<c>38-POLICIES.md §5.1</c>; DL-017). Um Run tem no máximo um snapshot;
/// nunca é actualizado, pelo que alterações posteriores à policy não alteram
/// retroactivamente o Run.
/// </summary>
public sealed class RecognitionPolicySnapshotEntity
{
    public long Id { get; set; }

    /// <summary>RunId a que o snapshot pertence (único).</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Versão do resolvedor que produziu o snapshot (ex.: <c>rp1</c>).</summary>
    public string ResolverVersion { get; set; } = string.Empty;

    /// <summary>Payload JSON com as políticas resolvidas (sem segredos).</summary>
    public string PoliciesJson { get; set; } = "{}";

    public DateTime ResolvedAtUtc { get; set; }
}
