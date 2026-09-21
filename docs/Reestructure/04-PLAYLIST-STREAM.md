# 04 — Playlist e Stream

## 1. Playlist

Playlist é um artefacto de uma Source que contém entradas de media.

Uma playlist pode ser adquirida novamente e deve ser tratada como observação/versionamento, não como identidade permanente do canal.

## 2. Stream

Stream representa uma entrada reproduzível de media.

Campos conceptuais mínimos:
- id;
- source/playlist;
- endereço ou referência segura;
- nome original;
- tvg-id original;
- tvg-name original;
- logo original;
- group-title original;
- media classification;
- normalized identity fields;
- fingerprint;
- timestamps;
- estado.

## 3. Normalização

Normalização deve ser determinística e não deve alterar silenciosamente a evidência original.

Guardar original e normalizado separadamente permite auditoria.

| Campo | Normalização |
|---|---|
| `name` | NFKC + trim + collapse de whitespace + casefold + remoção de diacríticos |
| `tvg-id` | NFKC + trim; casefold apenas para comparação, preservando o valor original |
| `group` | NFKC + trim + collapse de whitespace + casefold + remoção de diacríticos |
| `logo` | preservar original; canonicalização apenas segundo regras URL explícitas |
| `language` | representação canónica de idioma, preservando valor original |
| `codec` | trim + casefold; sem equivalências semânticas implícitas |
| `quality` | trim + casefold; sem equivalências semânticas implícitas |
| `URL` | canonicalização apenas dos componentes explicitamente declarados como não-identificadores |

Preservar sempre o valor raw/original; normalização destrutiva não pode alterar identidade funcional; manter separados Normalization, Recognition e Fingerprint.

## 4. Fingerprint

**Fingerprint de stream.** O fingerprint é uma representação canónica, determinística e versionada de um conjunto explícito de campos técnicos definido normativamente. NÃO depende de `CanonicalChannel`, de posição/ordenação da playlist, nem de qualidade, e NÃO inclui credenciais em claro. A codificação da representação canónica é UTF-8 e o hash é SHA-256; o resultado é serializado de forma determinística. A representação canónica DEVE declarar campos, ordem, separadores, encoding e tratamento de null. A versão pertence ao algoritmo. Uma alteração incompatível cria nova versão e estratégia de coexistência/migração (DL-108). A BÍBLIA é a autoridade normativa desta definição; não é exigido ADR.

A canonicalização de URL deve remover apenas elementos explicitamente definidos como não-identificadores. Nunca se deve remover informação de autenticação por simples normalização e depois reconstruir uma URL insegura.

### 4.1 Representação canónica do URL (versão `sfp1`)

A versão inicial do algoritmo é `sfp1`. A representação canónica é determinística, em UTF-8, e a serialização do material de hash é `version + "\n" + canonicalUrl` (separador explícito, sem concatenação ambígua). O `Fingerprint` é o hex minúsculo de `SHA-256` desse material. Só o fingerprint e a versão são persistidos; o URL canónico nunca é persistido.

Elementos da representação canónica:

- apenas URLs absolutos `http`/`https` são fingerprintáveis; qualquer outro caso não produz fingerprint;
- `scheme` e `host` em minúsculas; ponto final do host removido; porta por omissão removida (`80` em `http`, `443` em `https`); porta não-omissa preservada;
- fragmento sempre removido; `userinfo` nunca incluído;
- `path` preservado case-sensitive, sem descodificar percent-encoding (`%2F` é distinto de `/`);
- `path` Xtream `/live|movie|series/<USER>/<PASS>/<ID>` preserva `<ID>` e mascara `<USER>`/`<PASS>` como `***` (e.g. `/live/***/***/<ID>`); paths não-Xtream ficam inalterados;
- `query`: são removidos apenas os parâmetros de credencial `username`, `password`, `token` e `authorization`; os restantes parâmetros são preservados verbatim e pela ordem original (sem reordenação nesta versão).

A representação canónica e o material de hash nunca contêm password, token, `Authorization` ou username de credencial. Uma alteração incompatível desta representação cria uma nova versão (DL-108).

**Nota de reconciliação (conflito preservado).** A formulação histórica de `16-PERSISTENCE.md` e `32-DOMAIN-SCHEMA.md` indicava unicidade por `(CanonicalChannelId, SourceId)`, o que contradizia `07-SOURCES.md §5` (múltiplas streams candidatas por `ChannelSource`). O conflito é resolvido a favor de múltiplas streams: a identidade lógica persistente é `CanonicalChannelId + SourceId + Fingerprint + FingerprintVersion`, e o fingerprint distingue as streams equivalentes dentro da mesma Source.

## 5. Duplicados

Streams duplicadas na mesma Source devem ser consolidadas quando representam a mesma entrada segundo o fingerprint (mesma Source, mesmo `CanonicalChannel`, mesmo `Fingerprint` + `FingerprintVersion`). A consolidação nunca atravessa Sources.

Streams iguais em Sources diferentes não devem ser confundidas: a origem continua relevante.

## 6. Limites

Parsing deve impor:
- tamanho máximo de documento;
- número máximo de entradas;
- comprimento máximo de campos;
- tempo máximo de parsing;
- cancelamento.

Estes limites devem ser configuráveis dentro de limites seguros.

## 7. Parsing M3U

O input mínimo aceite exige `#EXTM3U` + `#EXTINF` + URL; extensões benignas podem ser reconhecidas, mas estrutura arbitrária não se torna playlist válida.

Uma entrada malformada DEVE ser registada e o parsing DEVE continuar com flag de resultado parcial; uma entrada inválida não invalida automaticamente toda a playlist.

`Playlist.Status` usa `Success` / `Partial` / `Failed`, e `Partial` nunca equivale a sucesso.

Distinguir estrutura da entrada de validade do alvo: uma entrada estruturalmente válida com URL não utilizável não é uma entrada M3U malformada.

Existem limites de tamanho/contagem configuráveis; os valores concretos são parâmetros operacionais, não normativos.
