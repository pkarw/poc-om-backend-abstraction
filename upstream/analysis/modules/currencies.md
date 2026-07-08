# Port contract — `currencies`

Upstream: `packages/core/src/modules/currencies/` (pinned `adc9da2`). Technology-agnostic PORT CONTRACT.

Currencies provides **currency reference data + exchange rates + conversion**. Its main downstream
consumer is the `customers` deal aggregate/summary endpoints, which resolve the **base currency**
(`is_base = true`) and convert per-currency deal sums into it via the exchange-rate service.

## 1. Entities / tables (exact DDL)

Three tables, all uuid PK `gen_random_uuid()`, all tenant+org scoped (`organization_id uuid NOT NULL`,
`tenant_id uuid NOT NULL`), `created_at`/`updated_at` `timestamptz`.

### `currencies`
| column | type | notes |
|---|---|---|
| id | uuid PK | `gen_random_uuid()` |
| organization_id | uuid NOT NULL | scope |
| tenant_id | uuid NOT NULL | scope |
| code | text NOT NULL | ISO 4217 (3 letters) |
| name | text NOT NULL | |
| symbol | text NULL | |
| decimal_places | int NOT NULL DEFAULT 2 | |
| thousands_separator | text NULL | |
| decimal_separator | text NULL | |
| is_base | boolean NOT NULL DEFAULT false | exactly one base per scope (enforced in command) |
| is_active | boolean NOT NULL DEFAULT true | |
| created_at / updated_at | timestamptz NOT NULL | |
| deleted_at | timestamptz NULL | soft delete |

Indexes: `currencies_scope_idx (organization_id, tenant_id)`;
unique `currencies_code_scope_unique (organization_id, tenant_id, code)`.

### `exchange_rates`
| column | type | notes |
|---|---|---|
| id | uuid PK | |
| organization_id / tenant_id | uuid NOT NULL | scope |
| from_currency_code | text NOT NULL | code, not FK |
| to_currency_code | text NOT NULL | code, not FK |
| rate | numeric(18,8) NOT NULL | stored/returned as string |
| date | timestamptz NOT NULL | rate-applies datetime (truncated to minute on write) |
| source | text NOT NULL | provider/source label |
| type | text NULL | `buy` \| `sell` (nullable) |
| is_active | boolean NOT NULL DEFAULT true | |
| created_at / updated_at | timestamptz NOT NULL | |
| deleted_at | timestamptz NULL | soft delete |

Indexes: `exchange_rates_scope_idx (organization_id, tenant_id)`;
`exchange_rates_pair_idx (from_currency_code, to_currency_code, date)`;
unique `exchange_rates_pair_datetime_source_unique (organization_id, tenant_id, from_currency_code, to_currency_code, date, source)`.

### `currency_fetch_configs` (DDL parity only — routes are PARITY-TODO)
Provider fetch scheduling: `provider text`, `is_enabled bool DEFAULT false`, `sync_time text NULL`,
`last_sync_at timestamptz NULL`, `last_sync_status/message text NULL`, `last_sync_count int NULL`,
`config jsonb NULL`. Indexes: scope idx, `enabled_idx (is_enabled, sync_time)`, unique
`provider_scope_unique (organization_id, tenant_id, provider)`. Created by the migration; no CRUD ported
(depends on the NBP/Raiffeisen web-scraping rate-fetching providers — out of scope).

## 2. Validation (`data/validators.ts`)
- currency code: trimmed, upper-cased, `^[A-Z]{3}$`.
- currency create: code, name (1..200), symbol (≤10 nullable), decimalPlaces int 0..8, separators (≤5),
  isBase, isActive.
- rate: string `^\d+(\.\d{1,8})?$` and `> 0`; from ≠ to; source 2..50 `[A-Za-z0-9\s\-_]`;
  `date` coerced then **truncated to minute**; type ∈ {buy,sell,null}.

## 3. Commands (write pipeline; go through the command bus)
`currencies.currencies.{create,update,delete}` and `currencies.exchange_rates.{create,update,delete}`,
all undoable, all emit persistent CRUD events.
- **create currency**: reject duplicate `code` in scope (409 conflict); if `is_base` demote every other
  base in scope in the same transaction (never 0 or 2 base currencies).
- **update currency**: 404 if missing; code-uniqueness check; setting `is_base=true` demotes others.
- **delete currency**: 400 if base; 400 if it has active exchange rates (from/to); else soft delete.
- **create rate**: both currencies must exist; reject duplicate (pair+date+source) 409; unique-violation
  mapped to 409.
- **update rate**: 404 if missing; currency existence + duplicate checks; final state `from ≠ to`,
  `rate > 0`.
- **delete rate**: 404 if missing; soft delete.

## 4. API routes (`makeCrudRoute` → CRUD factory)
- `/api/currencies` GET(list)/POST/PUT/DELETE — features
  GET `currencies.view`, mutations `currencies.manage`.
  List filters: `search` (code/name/symbol ilike), `code`, `isBase`, `isActive`; sort
  code/name/createdAt/updatedAt; default sort `code ASC`, pageSize 50.
- `/api/currencies/options` GET — `{items:[{value:code,label:"CODE - Name"}]}`, feature `currencies.view`.
- `/api/exchange-rates` GET/POST/PUT/DELETE — features GET `currencies.rates.view`, mutations
  `currencies.rates.manage`. List filters: `fromCurrencyCode`, `toCurrencyCode`, `isActive`, `source`,
  `type`; sort from/to/date/createdAt/updatedAt; default sort `date DESC`.
- Envelope `{items,total,page,pageSize,totalPages}`; create → 201 `{id}`; update/delete → `{ok:true}`.

## 5. Conversion service (`services/exchangeRateService.ts`)
`getRate({from,to,date,scope,options:{maxDaysBack=30,autoFetch=true}})`:
- validate date not in future; `from==to` throws; normalize date to UTC-midnight.
- find exact active rows (org+tenant+pair+date), else (autoFetch) fetch via providers, else go back one
  day, recursively up to `maxDaysBack` (total `maxDaysBack+1` checks). Returns all provider rows for the
  matched date.
`getRates({pairs,...})` batches; per-pair errors captured in the result (not thrown).
**Consumer pattern (customers deals):** resolve base currency (`is_base=true, deleted_at is null`), fetch
rates `code → base` for all distinct non-base currencies, multiply amount by the first rate → base total.

## 6. ACL (`acl.ts`) + setup
Features: `currencies.view`, `currencies.manage` (deps view), `currencies.rates.view` (deps view),
`currencies.rates.manage` (deps rates.view), `currencies.fetch.view` (deps view), `currencies.fetch.manage`
(deps fetch.view). `setup.ts`: `defaultRoleFeatures = { admin: ['currencies.*'] }`.

## 7. Seed (`lib/seeds.ts` / `setup.ts` seedDefaults, per tenant+org)
Idempotent upsert of 10 currencies (USD base): USD($, base), EUR(€), JPY(¥, 0dp), GBP(£), CHF(Fr, `'`
thousands), CAD(C$), AUD(A$), CNY(¥), CNH(¥), PLN(zł, space thousands). Existing rows only get `name`
refreshed.

## 8. Events (`events.ts`)
`currencies.currency.{created,updated,deleted}`, `currencies.exchange_rate.{created,updated,deleted}`.

## .NET port notes / deviations
- CRUD via `CrudRoute.Map<TEntity>` + `CommandBus` (guidance). List GET uses the generic factory GET;
  its empty-scope envelope reports `totalPages: 0` where the upstream custom handler used `max(1, …)` —
  minor, documented (ADR).
- `rate` modeled as `decimal` (numeric(18,8)); projected to string. Exact trailing-zero formatting is a
  PARITY-TODO.
- Rate-fetching providers (NBP/Raiffeisen scraping) + `currency_fetch_configs` routes + i18n/backend UI
  are out of scope (PARITY-TODO); `autoFetch` is a no-op seam over the DB fallback.
- `.NET` exposes `IExchangeRateService.ConvertToBaseAsync(amount, fromCode, date, scope)` — the
  ConvertToBase-style API customers call.
