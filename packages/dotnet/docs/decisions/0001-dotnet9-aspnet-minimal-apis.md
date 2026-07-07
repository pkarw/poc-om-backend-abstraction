# 0001 — .NET 9 with ASP.NET Core Minimal APIs

## Status

Accepted

## Context

The port needs an HTTP host whose routing model can mirror upstream Open Mercato's
file-convention routes (`packages/core/src/modules/<module>/api/<method>/<path>.ts`,
each file exporting one handler per HTTP method) with byte-compatible paths,
status codes and JSON payloads.

## Decision

Use .NET 9 with ASP.NET Core minimal APIs. Each module maps its routes explicitly
(`routes.MapGet("/api/health_check", ...)`) inside `IModule.MapRoutes`, giving a
one-route-per-handler shape that corresponds 1:1 to upstream route files.
System.Text.Json's web defaults (camelCase properties) reproduce upstream JSON
shapes without per-endpoint configuration.

## Consequences

- Route registration is explicit code, not filesystem discovery; the mapping
  from upstream route files to `MapRoutes` calls is mechanical and reviewable.
- MVC controllers/attribute routing are intentionally avoided to keep handlers
  small and close to the upstream one-file-one-route style.
- .NET 9 is an STS release; upgrading to .NET 10 LTS later is a version bump in
  csproj files and Docker image tags.
