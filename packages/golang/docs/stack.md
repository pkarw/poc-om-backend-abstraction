# Go package stack

Pinned in `go.mod`. Versions are floors — `go mod tidy` may select compatible newer patches.

| Component | Choice | Version | Rationale (one line) |
|---|---|---|---|
| Language / runtime | Go | 1.23 | Current stable; single static binary suits the two-host (api/worker) model. |
| HTTP router | [chi](https://github.com/go-chi/chi) | v5.1.0 | stdlib-`net/http`-compatible router with subrouter mounting — maps cleanly to per-module routers. |
| PostgreSQL driver | [pgx](https://github.com/jackc/pgx) | v5.7.1 | Fastest, best-maintained Postgres driver; no ORM keeps schemas byte-identical to upstream. |
| Migrations | [golang-migrate](https://github.com/golang-migrate/migrate) | v4.18.1 | Plain up/down SQL files — schema parity with upstream is reviewable as SQL diffs. |
| Redis client | [go-redis](https://github.com/redis/go-redis) | v9.7.0 | Official Redis client; used for cache and the BullMQ-style queue implementation. |
| Validation | [go-playground/validator](https://github.com/go-playground/validator) | v10.23.0 | Struct-tag validation — the idiomatic Go equivalent of upstream's Zod schemas. |
| .env loading | [godotenv](https://github.com/joho/godotenv) | v1.5.1 | Same `.env` dev workflow as the Node upstream; real env always wins. |
| Testing | stdlib `testing` | — | No assertion framework needed; keeps the toolchain zero-dependency. |
| Queue protocol | own `JobQueue` abstraction | — | BullMQ-style Redis keys today, full wire compatibility tracked in [ADR 0003](decisions/0003-bullmq-queue-compatibility.md). |
