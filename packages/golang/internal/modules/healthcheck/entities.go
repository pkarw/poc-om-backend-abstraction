package healthcheck

import "time"

// HealthPing maps table om_health_ping (migrations/0001_health_check_ping.up.sql).
// Entities are plain structs + hand-written SQL via pgx — the Go counterpart
// of upstream's MikroORM entities (modules/<module>/data/entities.ts).
// Column names stay snake_case, identical to upstream Postgres schemas.
type HealthPing struct {
	ID       int64     // id BIGSERIAL PRIMARY KEY
	Source   string    // source TEXT NOT NULL
	PingedAt time.Time // pinged_at TIMESTAMPTZ NOT NULL DEFAULT now()
}
