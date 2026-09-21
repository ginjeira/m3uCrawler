# 41 — Inventário funcional da API

Este inventário define a superfície funcional da API. Os paths podem ser versionados, mas cada operação deve possuir uma ficha normativa completa em `22-API-CONTRACTS.md` antes da implementação final. Uma linha deste inventário não é, por si só, um contrato implementável.

## System
- lifecycle/readiness;
- health;
- version.

## Auth
- login;
- logout;
- current user;
- CSRF/session.

## Telegram
- configuration;
- auth status;
- start;
- submit code;
- submit password;
- disconnect/logout session.

## Sources
- list;
- create/update;
- enable/disable;
- test;
- status.

## Discovery
- start run;
- list candidates;
- candidate details;
- accept/reject.

## Catalogue
- channels;
- channel details;
- aliases;
- external identities;
- imports/exports.

## Country
- `GET /api/countries`;
- `GET /api/country`;
- `GET /api/country/validate`;
- `POST /api/country/save`.

## Review
- list;
- details;
- resolve;
- ignore;
- reopen.

## Validation
- run;
- observations;
- eligibility.

## Policies
- CRUD;
- effective policy preview.

## Ordering
- lists;
- items;
- import/export;
- preview.

## Runs
- start;
- cancel;
- status;
- history;
- artifacts.

## Playlists
- generated outputs;
- download/serve;
- validation result.

## Dispatcharr
- configuration;
- test connection;
- dry-run;
- sync;
- reconciliation;
- ownership view.

Every mutating endpoint requires authentication, authorization and audit where specified by the domain.
