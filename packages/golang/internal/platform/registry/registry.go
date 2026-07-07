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

// Declaration surfaces (spec 10 — module contract parity). These mirror the
// upstream per-module convention files that *declare* — but do not themselves
// deliver — cross-cutting surfaces. Each is plain data on the Module so the
// registry can aggregate it in enabled-module order. "Declare now, engine
// later": the storage/delivery engine behind each may be a PORT-TODO until the
// owning module (notifications / entities) is ported.

// NotificationType mirrors upstream notifications.ts NotificationTypeDefinition.
type NotificationType struct {
	Type              string // stable id
	Module            string // owning module id
	Severity          string
	TitleKey          string
	Icon              string
	ExpiresAfterHours int
	LinkHref          string
}

// CustomField is one field in a custom-field set (upstream CustomFieldDefinition).
type CustomField struct {
	Key      string
	Kind     string
	Label    string
	Required bool
}

// CustomFieldSet mirrors upstream data/fields.ts CustomFieldSet.
type CustomFieldSet struct {
	EntityID string // "<module>:<entity>" target
	Source   string // declaring module id
	Fields   []CustomField
}

// CustomEntity mirrors an upstream ce.ts custom entity (fields feed field sets).
type CustomEntity struct {
	ID          string
	Label       string
	Description string
	Fields      []CustomField
}

// EventDef mirrors an upstream events.ts declared event. Name is the byte-exact
// "<module>.<entity>.<verb>" id; Persistent marks durable (queue-backed) events;
// ClientBroadcast marks SSE-forwarded ones.
type EventDef struct {
	Name            string
	Persistent      bool
	ClientBroadcast bool
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
	// NotificationTypes are declared notification types (upstream notifications.ts).
	NotificationTypes []NotificationType
	// CustomFieldSets are declared custom-field sets (upstream data/fields.ts).
	CustomFieldSets []CustomFieldSet
	// CustomEntities are declared custom entities (upstream ce.ts).
	CustomEntities []CustomEntity
	// DeclaredEvents are the module's declared, typed events (upstream events.ts).
	DeclaredEvents []EventDef
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

// --- Declaration-surface aggregators (spec 10 §MODCONTRACT-R7) ------------
// Pure folds over the enabled-module list, in enabled-module order.

// NotificationTypes returns every module's declared notification types.
func NotificationTypes(mods []Module) []NotificationType {
	var out []NotificationType
	for _, m := range mods {
		out = append(out, m.NotificationTypes...)
	}
	return out
}

// CustomFieldSets returns every module's declared custom-field sets.
func CustomFieldSets(mods []Module) []CustomFieldSet {
	var out []CustomFieldSet
	for _, m := range mods {
		out = append(out, m.CustomFieldSets...)
	}
	return out
}

// CustomEntities returns every module's declared custom entities.
func CustomEntities(mods []Module) []CustomEntity {
	var out []CustomEntity
	for _, m := range mods {
		out = append(out, m.CustomEntities...)
	}
	return out
}

// DeclaredEvents returns every module's declared events.
func DeclaredEvents(mods []Module) []EventDef {
	var out []EventDef
	for _, m := range mods {
		out = append(out, m.DeclaredEvents...)
	}
	return out
}
