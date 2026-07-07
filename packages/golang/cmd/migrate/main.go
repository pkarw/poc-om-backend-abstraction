// Command migrate applies all pending SQL migrations from ./migrations
// (override with MIGRATIONS_DIR) against DATABASE_URL, then exits.
package main

import (
	"log/slog"
	"os"

	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/config"
	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/db"
)

func main() {
	cfg, err := config.Load()
	if err != nil {
		slog.Error("migrate: config", "error", err)
		os.Exit(1)
	}
	dir := os.Getenv("MIGRATIONS_DIR")
	if dir == "" {
		dir = "migrations"
	}
	if err := db.Migrate(cfg.DatabaseURL, dir); err != nil {
		slog.Error("migrate: failed", "dir", dir, "error", err)
		os.Exit(1)
	}
	slog.Info("migrate: database is up to date", "dir", dir)
}
