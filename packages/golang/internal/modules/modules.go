// Package modules is the single module registration point — the Go
// counterpart of upstream's generated module registry. Every ported module
// gets one line in All(). Order matters only if modules depend on each other
// (infrastructure tier first, matching MODULES.md).
package modules

import (
	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/modules/healthcheck"
	"github.com/open-mercato/poc-om-backend-abstraction/packages/golang/internal/platform/registry"
)

// All builds every enabled module with the shared dependencies.
func All(deps *registry.Deps) []registry.Module {
	return []registry.Module{
		healthcheck.Module(deps),
	}
}
