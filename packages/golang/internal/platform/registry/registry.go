// Package registry is the Go counterpart of upstream's module auto-discovery
// (packages/core/src/modules/* + the generated module registry). Go has no
// filesystem-based discovery, so modules are declared explicitly in
// internal/modules/modules.go — that file is the single registration point.
package registry

import (
	"context"
	"encoding/json"

	"github.com/go-chi/chi/v5"
	"github.com/jackc/pgx/v5/pgxpool"
	"github.com/redis/go-redis/v9"

	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/config"
	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/events"
	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/queue"
)

// Deps is the DI container handed to every module (upstream: Awilix di.ts).
type Deps struct {
	Config *config.Config
	DB     *pgxpool.Pool
	Redis  *redis.Client
	Queue  queue.JobQueue
	Events *events.Bus
}

// Worker binds a job name on a queue to a handler
// (upstream: modules/<module>/workers/*.ts).
type Worker struct {
	Queue   string
	JobName string
	Handler queue.Handler
}

// Subscriber binds an event name to a handler
// (upstream: modules/<module>/subscribers/*.ts).
type Subscriber struct {
	Event   string
	Handler func(ctx context.Context, payload json.RawMessage) error
}

// Module is everything one Open Mercato module contributes to the runtime.
type Module struct {
	// ID is the upstream module id in snake_case, e.g. "health_check".
	ID string
	// Routes registers the module's HTTP handlers on the shared /api subrouter
	// (upstream: modules/<module>/api/<method>/<path>.ts).
	Routes func(r chi.Router)
	// Workers are queue processors hosted by cmd/worker.
	Workers []Worker
	// Subscribers are event handlers wired into the event bus.
	Subscribers []Subscriber
	// Features are ACL feature ids (upstream: modules/<module>/acl.ts).
	Features []string
}

// MountAll registers every module's routes on the /api subrouter.
func MountAll(api chi.Router, mods []Module) {
	for _, m := range mods {
		if m.Routes != nil {
			m.Routes(api)
		}
	}
}

// SubscribeAll wires every module's subscribers into the event bus.
func SubscribeAll(bus *events.Bus, mods []Module) {
	for _, m := range mods {
		for _, s := range m.Subscribers {
			bus.Subscribe(s.Event, s.Handler)
		}
	}
}

// WorkersByQueue groups all module workers as queue -> jobName -> handler,
// the shape the worker host consumes.
func WorkersByQueue(mods []Module) map[string]map[string]queue.Handler {
	out := make(map[string]map[string]queue.Handler)
	for _, m := range mods {
		for _, w := range m.Workers {
			if out[w.Queue] == nil {
				out[w.Queue] = make(map[string]queue.Handler)
			}
			out[w.Queue][w.JobName] = w.Handler
		}
	}
	return out
}

// Features returns the union of all modules' ACL feature ids.
func Features(mods []Module) []string {
	var out []string
	for _, m := range mods {
		out = append(out, m.Features...)
	}
	return out
}
