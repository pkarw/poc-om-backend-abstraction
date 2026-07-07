-- Module: health_check (reference module)
-- Minimal table used by GET /api/health_check to prove the migration pipeline
-- and database connectivity work end-to-end.
CREATE TABLE IF NOT EXISTS om_health_ping (
    id BIGSERIAL PRIMARY KEY,
    source TEXT NOT NULL DEFAULT 'system',
    pinged_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

INSERT INTO om_health_ping (source) VALUES ('migration');
