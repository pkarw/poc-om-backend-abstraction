package db

import (
	"database/sql"
	"errors"
	"path/filepath"

	"github.com/golang-migrate/migrate/v4"
	migratepgx "github.com/golang-migrate/migrate/v4/database/pgx/v5"
	_ "github.com/golang-migrate/migrate/v4/source/file" // file:// source driver
	_ "github.com/jackc/pgx/v5/stdlib"                   // database/sql driver "pgx"
)

// Migrate applies all pending SQL migrations from dir (e.g. "migrations").
// It is idempotent: an up-to-date database is not an error.
func Migrate(databaseURL, dir string) error {
	absDir, err := filepath.Abs(dir)
	if err != nil {
		return err
	}

	sqlDB, err := sql.Open("pgx", databaseURL)
	if err != nil {
		return err
	}
	defer sqlDB.Close()

	driver, err := migratepgx.WithInstance(sqlDB, &migratepgx.Config{})
	if err != nil {
		return err
	}

	m, err := migrate.NewWithDatabaseInstance("file://"+absDir, "postgres", driver)
	if err != nil {
		return err
	}

	if err := m.Up(); err != nil && !errors.Is(err, migrate.ErrNoChange) {
		return err
	}
	return nil
}
