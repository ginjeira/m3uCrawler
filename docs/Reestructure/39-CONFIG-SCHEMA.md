# 39 — Contrato de configuração

## 1. Princípio

Configuração técnica, configuração funcional e secrets são classes distintas.

## 2. Configuração técnica

Inclui:
- paths;
- ports;
- logging;
- limits;
- scheduler (incluindo timezone de agendamento);
- output.

Timezone de agendamento é configuração técnica; timestamps persistidos permanecem UTC.

## 3. Configuração funcional

Inclui:
- providers;
- sources;
- policies;
- ordering;
- Dispatcharr enablement.

## 4. Secrets

Inclui:
- Telegram api hash;
- phone/session credentials;
- provider credentials;
- Dispatcharr credentials;
- tokens.

Nunca são tratados como metadados públicos.

## 5. Precedência normativa

Para **configuração técnica não persistida**:

`defaults < config file < environment < CLI`.

A configuração funcional persistida pela aplicação não participa desta cadeia como se fosse mais um ficheiro de configuração. É estado administrado pelo produto e sobrevive a restart.

Uma alteração por CLI/environment a estado funcional persistente só ocorre através de uma operação explicitamente definida para esse efeito; não pode ser uma consequência silenciosa do arranque.

Propriedades funcionais persistidas têm SQLite como autoridade. CLI/ENV NÃO podem alterar silenciosamente estado funcional no arranque. Alteração funcional através de CLI/ENV exige uma operação administrativa explícita.

Quando uma propriedade puder existir tanto como configuração técnica como como estado funcional, a BÍBLIA deve declarar qual das duas classes é a autoridade. O implementador não deve inferir a resposta.

## 6. Runtime

O Run usa um ConfigurationSnapshot resolvido no início.

## 7. Escrita

Writers:
- validam;
- escrevem temporariamente;
- fazem replace atómico;
- preservam dados desconhecidos quando compatível;
- aplicam permissões restritas;
- nunca imprimem secrets.
