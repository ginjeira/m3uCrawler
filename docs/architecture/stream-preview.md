# Stream preview engine (pré-visualização de canais)

> Estado: implementado (Fase 1b / W-PREVIEW). Documento **descritivo** do motor
> server-side de pré-visualização; não é uma especificação normativa nem substitui
> a documentação operacional.
>
> Fonte canónica do comportamento, das flags e das limitações:
> `m3uCrawler/README.md` § "Pré-visualização de canais e player de playlists" e
> § "Argumentos da linha de comando". Este documento referencia-a e **não** a duplica.

## Objectivo

Dar **confirmação visual** antes de uma decisão humana, em dois pontos de uso:

- **Reviews** — ver o(s) stream(s) candidato(s) de uma review (tabela
  `review_candidates`).
- **Player** — abrir `playlist.m3u`, `playlist_temp.m3u` ou `playlist_acquired.m3u`
  e reproduzir um canal.

O objectivo transversal é permitir a reprodução **sem nunca expor** a URL Xtream
(com credenciais) ao browser, ao DOM ou aos logs.

## Componentes

| Componente | Ficheiro | Papel |
|---|---|---|
| `StreamReferenceResolver` | `Services/Preview/StreamReferenceResolver.cs` | Traduz a referência interna do cliente numa `StreamReference` (URL server-side). |
| `StreamProbeService` | `Services/Preview/StreamProbeService.cs` | Corre `ffprobe`, traduz o JSON para `StreamProbe` e mantém cache em memória. |
| `PreviewSessionManager` | `Services/Preview/PreviewSessionManager.cs` | Cria/termina sessões, controla concorrência, *idle timeout*, TTL e teardown. |
| `FfmpegArguments` / `ProcessRunner` | `Services/Preview/FfmpegArguments.cs`, `ProcessRunner.cs`, `IProcessRunner.cs` | Construção pura dos argumentos de `ffmpeg`/`ffprobe` e execução dos processos. |
| Rotas HTTP | `Services/WebDashboardService.cs` | `/api/playlist-entries`, `/api/playlist-entry`, `/api/preview/*`, `/assets/hls.min.js`. |
| Wiring | `Program.cs` | Flags de preview + construção do `PreviewSessionManager` (bloco `--web`). |

## Fluxo

```
ref (browser)  --POST /api/preview/start { ref }-->
   │
   ▼
StreamReferenceResolver          "review:<id>" | "review:<id>:<cid>" | "playlist:<which>:<id>"
   │  (URL real resolvida server-side; nunca devolvida)
   ▼
StreamProbeService (ffprobe)
   │
   ├─ PlayableByBrowser == true  --> ffmpeg HLS remux (-c copy) ........ kind "hls"
   ├─ TranscodeNonPlayable == true --> ffmpeg HLS transcode (H.264/AAC) . kind "hls"
   └─ caso contrário            --> ffmpeg frame snapshot (JPEG) ........ kind "snapshot"
   │
   ▼
sessão efémera em <runtime-data>/preview/<sessionId>/  (index.m3u8 + seg%05d.ts | thumb.jpg)
   │
   ▼
browser: GET /api/preview/<id>/index.m3u8 (+ segmentos) via hls.js, ou /api/preview/<id>/thumb.jpg
```

Detalhes relevantes do motor (verificados no código):

- A decisão de *playable* é uma heurística de codecs: vídeo ∈ {`h264`, `vp8`, `vp9`,
  `av1`} e áudio ∈ {`aac`, `opus`, `vorbis`, `mp3`} (sem faixa de áudio é considerado
  tocável). Containers HLS/MPEG-TS são irrelevantes: o remux resolve-os.
- O remux produz `index.m3u8` + segmentos `seg%05d.ts` (`-hls_time 2`,
  `-hls_list_size 6`, delete/append, sem `#EXT-X-ENDLIST` — ao vivo).
- O *snapshot* captura 1 frame a ~1 s, redimensionado para 480 px de largura.
- A sondagem (`ffprobe`) fica em cache em memória, indexada pelo **hash SHA-256 do
  URL** (o URL em claro nunca é chave nem é registado); TTL padrão 10 min.

## Ciclo de vida das sessões

- **On-demand**: a sessão nasce de um `POST /api/preview/start` e existe só enquanto
  for necessária.
- **Concorrência**: um semáforo limita o número de sessões simultâneas
  (`--preview-max-concurrent`, padrão 2). Esgotado o limite, `start` devolve `429`
  (`preview-busy`) — a referência é resolvida antes, mas nenhuma sessão é criada.
- **Expiração**: um *sweeper* de fundo termina sessões por inactividade
  (`--preview-idle-seconds`, padrão 20 s) ou por TTL máximo
  (`--preview-max-duration-seconds`, padrão 300 s). Cada leitura de segmento/thumb e
  cada `start` renovam a actividade.
- **Teardown**: ao terminar (por `POST /api/preview/<id>/stop`, expiração ou
  *shutdown*), o processo `ffmpeg` é morto e o directório da sessão é removido
  (melhor esforço). Todas as sessões são encerradas no *shutdown* ordenado.
- **Robustez**: o motor não assume que o handle do processo continua vivo e nunca
  lança por sessões desconhecidas (operações idempotentes).

## Segurança

- **Sessão-only.** Todas as rotas de preview exigem sessão humana (login + CSRF). O
  **token de máquina** das playlists (`/api/playlist*`) **não** autoriza previews.
- **Sem URLs/credenciais no cliente.** O browser só conhece referências internas
  (`review:<id>`, `review:<id>:<cid>`, `playlist:<which>:<id>`) e caminhos servidos
  localmente (`/api/preview/<id>/…`, `/assets/hls.min.js`). A URL real existe apenas
  dentro de `StreamReference`, consumida server-side.
- **Anti-SSRF.** O resolver aceita apenas a gramática das referências internas;
  URLs enviadas pelo cliente são rejeitadas. Não há fetches arbitrários do cliente.
- **Sem path traversal.** A resolução de ficheiros da sessão valida que o caminho
  final fica dentro do directório da sessão; caminhos absolutos e `..` são rejeitados.
- **Sem credenciais em logs.** Os argumentos (`-i <url>`) não são registados; o único
  diagnóstico é `StderrTail` já sanitizado.
- **CSP.** As respostas HTML do dashboard levam `Content-Security-Policy` (restrita a
  `'self'`, com `img-src … http: https:` para logos e `media-src 'self' blob:` para os
  segmentos do `hls.js`). A limitação de `'unsafe-inline'` está documentada no README.

## Limites

- **CPU**: remux é mais barato que transcode; o transcode (`--preview-transcode`,
  opt-in) é CPU-intensivo e compete com o pipeline.
- **Ligações Xtream**: cada sessão abre o stream no provedor; os previews são
  on-demand, curtos e limitados por `--preview-max-concurrent`.
- **Codecs não suportados** (ex.: HEVC/H.265, AC-3/E-AC-3): sem transcode, apenas
  *snapshot* + download da entrada (`.m3u`) para reprodução externa (VLC).
- **Logos** (`tvg-logo`) são recursos externos (`http`/`https`).

## Referências

- Comportamento, uso e flags: `m3uCrawler/README.md` § "Pré-visualização de canais e
  player de playlists" e § "Argumentos da linha de comando".
- Deployment e recursos (`ffmpeg` na imagem): `DEPLOYMENT.md` § 9.1.
- Histórico: `CHANGELOG.md` `[Unreleased]` (W-PREVIEW, 2026-10-15).
