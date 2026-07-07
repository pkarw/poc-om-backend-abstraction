package config

import "testing"

func TestLoadDefaults(t *testing.T) {
	t.Setenv("DATABASE_URL", "postgres://u:p@localhost:5432/db")
	t.Setenv("REDIS_URL", "redis://localhost:6379")
	t.Setenv("QUEUE_STRATEGY", "")
	t.Setenv("QUEUE_REDIS_URL", "")
	t.Setenv("PORT", "")

	cfg, err := Load()
	if err != nil {
		t.Fatalf("Load() error = %v", err)
	}
	if cfg.Port != "8090" {
		t.Errorf("Port = %q, want 8090", cfg.Port)
	}
	if cfg.QueueStrategy != "redis" {
		t.Errorf("QueueStrategy = %q, want redis", cfg.QueueStrategy)
	}
	if cfg.QueueRedisURL != cfg.RedisURL {
		t.Errorf("QueueRedisURL = %q, want fallback to REDIS_URL", cfg.QueueRedisURL)
	}
}

func TestLoadRequiresDatabaseURL(t *testing.T) {
	t.Setenv("DATABASE_URL", "")
	t.Setenv("REDIS_URL", "redis://localhost:6379")

	if _, err := Load(); err == nil {
		t.Fatal("Load() with empty DATABASE_URL should fail validation")
	}
}
