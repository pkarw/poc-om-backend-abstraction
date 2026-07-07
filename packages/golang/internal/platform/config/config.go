// Package config loads runtime configuration from environment variables,
// with optional .env support. Env var names match upstream Open Mercato exactly.
package config

import (
	"os"

	"github.com/joho/godotenv"

	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/validation"
)

// Config mirrors the canonical Open Mercato environment contract.
type Config struct {
	DatabaseURL   string `validate:"required"`
	RedisURL      string `validate:"required"`
	QueueStrategy string `validate:"oneof=local redis"`
	QueueRedisURL string
	JWTSecret     string
	Port          string
}

// Load reads .env (if present) and the process environment.
func Load() (*Config, error) {
	_ = godotenv.Load() // best effort; real env always wins in godotenv

	cfg := &Config{
		DatabaseURL:   os.Getenv("DATABASE_URL"),
		RedisURL:      os.Getenv("REDIS_URL"),
		QueueStrategy: getenv("QUEUE_STRATEGY", "redis"),
		QueueRedisURL: os.Getenv("QUEUE_REDIS_URL"),
		JWTSecret:     os.Getenv("JWT_SECRET"),
		Port:          getenv("PORT", "8090"),
	}
	if cfg.QueueRedisURL == "" {
		cfg.QueueRedisURL = cfg.RedisURL
	}
	if err := validation.Struct(cfg); err != nil {
		return nil, err
	}
	return cfg, nil
}

func getenv(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}
