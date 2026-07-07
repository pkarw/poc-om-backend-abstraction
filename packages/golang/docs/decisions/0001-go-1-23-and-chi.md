# 0001 — Go 1.23 with chi as the HTTP router

## Status

Accepted

## Context

The port needs an HTTP host that can mount per-module routers under `/api/...`
with paths, methods, status codes and JSON shapes identical to upstream's
Next.js route handlers (`packages/core/src/modules/<module>/api/<method>/<path>.ts`).
Candidates: stdlib `net/http` mux, chi, gin, echo, fiber.

## Decision

Go 1.23 (current stable) and `github.com/go-chi/chi/v5`.

- chi is 100% `net/http` compatible: handlers are plain `http.HandlerFunc`,
  so nothing framework-specific leaks into module code.
- `chi.Router` subrouters map 1:1 to "a module contributes a router" — the
  registry hands each module the `/api` subrouter and the module registers its
  own upstream-identical paths.
- gin/echo/fiber use custom context types (fiber doesn't even use `net/http`),
  which would couple every ported handler to a framework.

## Consequences

- Handlers stay portable, testable with `httptest`, and free of framework lock-in.
- Middleware (auth, logging) comes from the chi/stdlib ecosystem.
- No built-in request binding/validation — covered by encoding/json +
  go-playground/validator (see ADR 0002 context and `internal/platform/validation`).
