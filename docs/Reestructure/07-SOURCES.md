# 07 — Provider, Account, Source e ChannelSource

## 1. Provider

Representa o fornecedor/ecossistema técnico.

## 2. ProviderAccount

Representa uma conta concreta nesse provider.

Uma conta pode expor várias Sources.

## 3. Source

Representa uma origem que o operador quer processar.

Uma Source deve ter:
- id;
- provider;
- account;
- configuração necessária;
- enabled;
- estado operacional;
- metadados;
- timestamps.

**Regra:** se uma Source está configurada e enabled, o sistema considera que o operador quer que essa origem seja detectada/processada.

## 4. ChannelSource

É a relação entre:
`CanonicalChannel ↔ Source`

Pode conter:
- stream(s) observadas;
- identidade externa;
- estado;
- última observação;
- validação;
- eligibility;
- proveniência;
- timestamps.

Não é um CanonicalChannel alternativo.

## 5. Vários streams

Uma ChannelSource pode possuir múltiplas streams candidatas. Selection escolhe a que deve ser usada.

## 6. Desactivação

Desactivar uma Source impede novas execuções para essa origem, mas não deve apagar automaticamente o catálogo nem reescrever identidades históricas.
