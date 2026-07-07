// Package healthcheck is the reference module: the smallest possible module
// wired end-to-end through the registry (route + worker + subscriber + acl +
// entity + migration). Copy this package as the starting pattern when porting
// a real Open Mercato module.
package healthcheck

import (
	"github.com/go-chi/chi/v5"

	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/registry"
)

const (
	// ModuleID is the upstream-style snake_case module id.
	ModuleID = "health_check"
	// QueueName is the queue this module's worker listens on.
	QueueName = "health_check"
	// EventPinged is published after every successful health check.
	EventPinged = "health_check.pinged"
)

// Module assembles everything this module contributes to the runtime.
func Module(deps *registry.Deps) registry.Module {
	return registry.Module{
		ID: ModuleID,
		Routes: func(r chi.Router) {
			// upstream equivalent: modules/health_check/api/get/health_check.ts
			r.Get("/health_check", handleGetHealthCheck(deps))
		},
		Workers: []registry.Worker{
			{Queue: QueueName, JobName: "ping", Handler: handlePingJob},
		},
		Subscribers: []registry.Subscriber{
			{Event: EventPinged, Handler: onPinged},
		},
		Features: []string{"health_check.view"},
		// Declaration surfaces (spec 10). health_check declares none — empty,
		// not nil-by-omission, so the contract shape matches every module.
		NotificationTypes: []registry.NotificationType{},
		CustomFieldSets:   []registry.CustomFieldSet{},
		CustomEntities:    []registry.CustomEntity{},
		DeclaredEvents:    []registry.EventDef{},
	}
}
