// Command api is the HTTP host: it wires the platform (config, db, redis,
// queue, events), builds all modules through the registry, mounts their
// routers under /api and serves a dependency-free /healthz liveness probe.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/go-chi/chi/v5"
	"github.com/go-chi/chi/v5/middleware"
	"github.com/redis/go-redis/v9"

	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/modules"
	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/config"
	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/db"
	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/events"
	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/queue"
	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/redisconn"
	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/registry"
)

func main() {
	if err := run(); err != nil {
		slog.Error("api: fatal", "error", err)
		os.Exit(1)
	}
}

func run() error {
	cfg, err := config.Load()
	if err != nil {
		return err
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	pool, err := db.Connect(ctx, cfg.DatabaseURL)
	if err != nil {
		return err
	}
	defer pool.Close()

	rdb, err := redisconn.Connect(ctx, cfg.RedisURL)
	if err != nil {
		return err
	}
	defer rdb.Close()

	var queueClient *redis.Client
	if cfg.QueueStrategy == "redis" {
		queueClient, err = redisconn.Connect(ctx, cfg.QueueRedisURL)
		if err != nil {
			return err
		}
		defer queueClient.Close()
	}
	jq, err := queue.New(cfg.QueueStrategy, queueClient)
	if err != nil {
		return err
	}

	bus := events.New()
	deps := &registry.Deps{Config: cfg, DB: pool, Redis: rdb, Queue: jq, Events: bus}
	mods := modules.All(deps)
	registry.SubscribeAll(bus, mods)

	r := chi.NewRouter()
	r.Use(middleware.Logger, middleware.Recoverer)

	// Liveness: must not touch db/redis.
	r.Get("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusOK)
		_ = json.NewEncoder(w).Encode(struct {
			Status  string `json:"status"`
			Service string `json:"service"`
		}{Status: "ok", Service: "golang-api"})
	})

	r.Route("/api", func(api chi.Router) {
		registry.MountAll(api, mods)
	})

	srv := &http.Server{Addr: ":" + cfg.Port, Handler: r}
	errCh := make(chan error, 1)
	go func() {
		slog.Info("golang-api listening",
			"port", cfg.Port, "modules", len(mods), "features", registry.Features(mods))
		errCh <- srv.ListenAndServe()
	}()

	select {
	case err := <-errCh:
		if errors.Is(err, http.ErrServerClosed) {
			return nil
		}
		return err
	case <-ctx.Done():
		slog.Info("golang-api shutting down")
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		return srv.Shutdown(shutdownCtx)
	}
}
