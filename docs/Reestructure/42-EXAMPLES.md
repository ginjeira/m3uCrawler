# 42 — Exemplos normativos

## Exemplo A — canal conhecido em duas sources

MEO stream → Recognition → RTP1  
Vodafone stream → Recognition → RTP1

Resultado:
- um CanonicalChannel RTP1;
- duas ChannelSources;
- Selection escolhe uma stream;
- Ordering atribui a posição.

Não são criados dois canais.

## Exemplo B — canal desconhecido

Stream "Canal XYZ" sem correspondência suficiente.

Resultado:
- Stream preservada;
- ReviewItem Open;
- nenhum CanonicalChannel criado;
- nenhum output linear produzido a partir dela.

## Exemplo C — mesma posição, canais diferentes

Source A: posição 4 = TVI  
Source B: posição 4 = RTP2

Resultado:
- duas identidades diferentes;
- posição de origem não participa da identidade.

## Exemplo D — stream melhor

Duas streams reconhecidas como o mesmo canal:
- A = HD, priority 5;
- B = SD, priority 1.

Se a policy disser que priority domina qualidade, B é seleccionada.

Se a policy disser que HD domina priority, A é seleccionada.

A identidade continua a mesma.

## Exemplo E — Dispatcharr externo

Dispatcharr contém um canal que não foi criado pelo crawler.

O crawler pode observar e relatar, mas não o apaga automaticamente.

## Exemplo F — falha durante create

Create channel teve sucesso; create stream falhou.

Resultado:
- Run não é Success;
- recurso criado fica registado;
- reconciliação/compensação trata o recurso de forma segura;
- próxima execução consegue recuperar sem duplicar.
