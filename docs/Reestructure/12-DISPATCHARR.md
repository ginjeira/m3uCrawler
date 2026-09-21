# 12 — Dispatcharr

## 1. Papel

Dispatcharr é destino de sincronização. Não é fonte de verdade do catálogo.

O estado remoto observado do Dispatcharr é evidência do sistema externo; NÃO existe autoridade local sobre a verdade do estado remoto. A aplicação pode persistir a observação para histórico/reconciliação, mas uma cópia local nunca é a autoridade do estado real (mantém DL-013).

## 2. Dry-run

Dry-run calcula e reporta as alterações sem as aplicar.

O plano deve ser inspectável.

## 3. Ownership

Cada recurso criado pelo crawler deve possuir registo de ownership.

Estados mínimos:
- CrawlerManaged;
- External;
- Unknown.

Só `CrawlerManaged` pode ser removido automaticamente pelo crawler.

## 4. Reconciliação

O desired state é o plano calculado deterministicamente pela composição do pipeline no contexto do Run/Snapshot. Não é `GeneratedPlaylist` por si só, nem uma `DispatcharrPolicy` autónoma.

A sincronização deve:
1. ler estado remoto;
2. calcular intenção;
3. aplicar alterações permitidas;
4. registar resultado;
5. reconciliar o estado final.

## 5. Partial failure

Se criação de canal tiver sucesso e criação de stream falhar, a aplicação deve manter evidência do recurso criado e executar compensação segura quando possível.

Não deve assumir que uma chamada falhada significa que o recurso não existe.

## 6. Segurança

Credenciais nunca aparecem em logs/reports/artifacts.

URLs sensíveis devem ser sanitizadas em qualquer output destinado a diagnóstico.

## 7. Recursos externos

External/Unknown nunca devem ser apagados automaticamente.
