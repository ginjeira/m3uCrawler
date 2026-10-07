# 06 — País, locale e classificação de media

## 1. CountryProfile

Dados de país/locale suportam validação e classificação. Não são a autoridade de identidade canónica.

O perfil de Portugal pode conter indicadores, aliases e regras auxiliares, mas não pode implicitamente criar canais.

`CountryProfile` é a autoridade dos dados/classificação de país versionados; overlays são camadas subordinadas, não autoridades concorrentes.

## 2. Country Gate

O Country Gate responde à pergunta:

> "Esta entrada satisfaz os critérios de país/locale configurados para continuar?"

Não responde:

> "Que canal é este?"

## 3. MediaClassification

Cada entrada deve ser classificada como, no mínimo:
- Linear TV;
- Radio;
- VOD;
- Unknown.

A classificação pode ser derivada de evidência e política, mas o resultado deve ser explícito.

`MediaClassification` é a autoridade do resultado da classificação de media. A policy que produz a classificação é regra separada. Media não é definido por posição em OrderingList.

## 4. VOD

VOD é separado da televisão linear:
- não recebe posições de uma ordering list de TV;
- não deve competir com canais lineares;
- pode ter grupos e output próprios;
- só é publicado se a política VOD o permitir.

## 5. Country e media não substituem o catálogo

Country e media são filtros/classificadores. Não são identidades.
