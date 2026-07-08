# ADR 0018 — Currencies module deviations

Status: accepted
Date: 2026-07-08
Context: port of upstream `packages/core/src/modules/currencies` (pin adc9da2) into
`OpenMercato.Modules.Currencies`. Contract: `upstream/analysis/modules/currencies.md`.

The three tables (`currencies`, `exchange_rates`, `currency_fetch_configs`), the admin CRUD envelopes,
status codes, RBAC gating, the base-currency invariant, and the CRUD events are byte-exact with
upstream. Admin CRUD goes through the CRUD factory (`CrudRoute.Map`) + command bus per the .NET
conventions. The deviations below are internal or dependency-driven and marked `// PARITY-TODO` in code.

## 1. Rate-fetching providers + `currency_fetch_configs` routes out of scope (PARITY-TODO)

Upstream ships an NBP + Raiffeisen web-scraping `rateFetchingService` and a `currency_fetch_configs`
admin surface + fetch worker. These depend on outbound HTTP scraping/SSRF-guarded fetching and are not
ported. The `currency_fetch_configs` table is created for DDL parity only (no routes/commands). The
`exchangeRateService` `autoFetch` option is a **no-op seam** here: `GetRateAsync` performs the same
day-by-day fallback but only over the `exchange_rates` table (never fetches). This is a strict subset —
no behavioral change for rows already present.

## 2. `ConvertToBase` is exposed as a first-class service method

Upstream has no `convertToBase` function; consumers (customers deal aggregate/summary) resolve the base
currency (`is_base = true`) and call `exchangeRateService.getRates(pairs: code → base)` then multiply by
the first returned rate (see `services/README.md` `convertAmount`). The port packages exactly that
sequence into `IExchangeRateService.ConvertToBaseAsync(amount, fromCode, date, scope)` (+ a read-only
`GET /api/currencies/convert` endpoint) so customers can call one method. Same-as-base returns the amount
with `rate = 1`; no base or no rate returns `converted = false` with the amount unchanged (the degraded
path the customers endpoints already implement).

## 3. Exchange-rate list default order is ASC, not DESC (PARITY-TODO)

Upstream's custom `exchange_rates` GET defaults to `date DESC`. The generic CRUD-factory GET defaults to
ASC when no `sortDir` is supplied (`CrudListQueryParser`). Explicit `?sortDir=desc` reproduces the
upstream default; the `date` sorter itself is byte-exact for both directions. Documented rather than
special-cased to keep the factory generic.

## 4. Empty-scope list envelope reports `totalPages: 0`

Upstream's custom currencies GET used `Math.max(1, ceil(total/pageSize))`; the CRUD factory envelope
(`BuildEnvelope`) reports `totalPages: 0` for an empty result — the same shape every ported
factory-backed list uses (see ADR 0016 / CrudRouteTests). Minor and consistent across the .NET port.

## 5. `rate` modeled as `decimal` (numeric(18,8))

Upstream stores/returns `rate` as a string. The port models it as `decimal` (column `numeric(18,8)`) and
projects it back to a string via `ToString(InvariantCulture)`. Exact trailing-zero preservation of the
Postgres numeric scale (e.g. `"1.10000000"`) is a PARITY-TODO — the value is exact, only the string
formatting of trailing zeros can differ.

## 6. Unique-violation → 409 relies on the pre-check

Create/update commands pre-check duplicates (currency code; rate pair+date+source) and throw 409. The
DB unique-constraint race (two concurrent inserts) is not mapped to 409 here (upstream maps
`isUniqueViolation`); it would surface as a 500. PARITY-TODO — acceptable for the POC.

## 7. Seeding hooked into the API boot path

Upstream runs `setup.ts` `seedDefaults` per tenant+org during tenant setup. The port exposes
`CurrenciesSeeder.SeedExampleCurrenciesAsync(db, tenantId, orgId)` (byte-exact 10-currency USD-base list,
idempotent name refresh) and a boot hook `RunBootAsync` that seeds every existing organization scope
after migrations (alongside the directory init seeder). Same data, host-driven timing.
