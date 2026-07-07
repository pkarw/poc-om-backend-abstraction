// Command worker is the queue worker host: it connects to Redis, collects
// every module's workers through the registry, starts one consumer loop per
// queue and runs until SIGINT/SIGTERM.
package main

import (
	"context"
	"errors"
	"log/slog"
	"os"
	"os/signal"
	"syscall"

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
	if err := run(); err != nil && !errors.Is(err, context.Canceled) {
		slog.Error("worker: fatal", "error", err)
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

	byQueue := registry.WorkersByQueue(mods)
	queueNames := make([]string, 0, len(byQueue))
	for queueName, handlers := range byQueue {
		queueNames = append(queueNames, queueName)
		go func(queueName string, handlers map[string]queue.Handler) {
			dispatch := func(ctx context.Context, job *queue.Job) error {
				h, ok := handlers[job.Name]
				if !ok {
					slog.Warn("worker: no handler for job", "queue", queueName, "name", job.Name, "job_id", job.ID)
					return nil
				}
				return h(ctx, job)
			}
			if err := jq.Process(ctx, queueName, dispatch); err != nil && !errors.Is(err, context.Canceled) {
				slog.Error("worker: processor stopped", "queue", queueName, "error", err)
			}
		}(queueName, handlers)
	}

	slog.Info("golang-worker started",
		"strategy", cfg.QueueStrategy, "queues", queueNames, "modules", len(mods))
	<-ctx.Done()
	slog.Info("golang-worker shutting down")
	return nil
}
