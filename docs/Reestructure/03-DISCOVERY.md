# 03 — Discovery e aquisição

## 1. Conceitos

**DiscoveryCandidate** é uma possibilidade descoberta. Não é ainda uma Source aceite. É uma ocorrência de descoberta associada a um Run. Não é uma entidade viva persistente entre execuções; o histórico entre execuções é obtido através dos `Run` e das respectivas evidências/ocorrências/`Observation`. Um `DiscoveryCandidate` referencia o `ProviderAccountId` (ligação a `ProviderAccount`).

**Provider** identifica o serviço/ecossistema de origem.

**ProviderAccount** identifica uma conta descoberta/configurada dentro desse provider.

**Source** representa uma origem que o operador pretende que o crawler processe.

## 2. Fluxo

Discovery:
`Discovery mechanism → Candidate → normalization → deduplication → account identity → Source`

A passagem Candidate→Source deve ser explícita.

## 3. Deduplicação

A mesma conta descoberta através de várias evidências não deve originar múltiplos processamentos equivalentes.

A deduplicação DEVE usar uma identidade funcional estável da conta quando o provider a disponibiliza. `AccountKey` deve ser tratado como unidade de serialização sempre que várias operações sobre a mesma conta possam conflituar.

Não é permitido assumir que URL/ordem/linha de playlist identifica uma conta.

**AccountKey.** A identidade funcional de uma conta é composta por: `Provider namespace + external functional identity → AccountKey`. Quando o provider fornece uma identidade funcional estável, essa identidade DEVE ser usada. Quando não fornece, o `AccountKey` só PODE ser derivado de evidência funcional estável e determinística (incluindo o endpoint/base resource normalizado quando este fizer parte da identidade funcional, e outros elementos estáveis apenas quando necessários para distinguir contas). NUNCA usar posição, ordem da playlist, timestamp, URL de playlist com credenciais, ou password como componente de identidade persistente. Se a evidência disponível não permitir garantir estabilidade e distinção, NÃO criar silenciosamente uma identidade funcional; o comportamento nesse caso deve ser explicitamente conservador.

A identidade funcional (`AccountKey`) é distinta de identificadores técnicos (`ProviderAccountId`).

## 4. Aquisição

Aquisição deve:
- aplicar timeouts;
- limitar tamanho;
- respeitar cancelamento;
- classificar falhas;
- evitar logar segredos;
- produzir evidência suficiente para diagnóstico.

Credenciais nunca devem aparecer em logs, reports ou artifacts destinados ao operador.

## 5. Providers

A arquitectura deve permitir adapters por provider sem espalhar conhecimento específico pelo domínio.

O domínio não deve depender de detalhes de Telegram, Xtream ou outro mecanismo de discovery.

## 6. Xtream

Uma implementação Xtream deve modelar separadamente:
- endpoint/base URL;
- credencial/conta;
- capacidades do serviço;
- playlists/categorias/streams;
- resultado de validação.

A URL de uma stream com credenciais não é uma identidade de stream e não deve ser persistida/emitida sem uma política explícita de segredo.
