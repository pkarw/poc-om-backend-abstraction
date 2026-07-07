# .NET Port — Technology Stack

| Component        | Choice                                | Version | Rationale (one line)                                                        |
| ---------------- | ------------------------------------- | ------- | --------------------------------------------------------------------------- |
| Runtime          | .NET                                  | 9.0     | Current STS with mature minimal APIs and first-class container images.      |
| HTTP framework   | ASP.NET Core minimal APIs             | 9.0     | Route-per-handler style maps 1:1 to upstream `api/<method>/<path>.ts`.      |
| ORM              | EF Core + Npgsql provider             | 9.0.4   | MikroORM equivalent with LINQ, change tracking and a real migration engine. |
| Migrations       | EF Core migrations (`dotnet ef`)      | 9.0.4   | Code-first migrations checked into the repo, applied on API startup.        |
| Redis client     | StackExchange.Redis                   | 2.8.16  | De-facto standard .NET Redis client (multiplexed, async).                   |
| Validation       | FluentValidation                      | 11.11.0 | Declarative rule DSL, the idiomatic .NET counterpart of Zod.                |
| Queue            | Own `IJobQueue` + Redis impl          | —       | No official BullMQ .NET client; see ADR 0004 for the compatibility plan.    |
| Tests            | xunit                                 | 2.9.2   | Standard .NET test framework.                                               |
| DI               | Microsoft.Extensions.DependencyInjection | 9.0  | Built-in container replaces Awilix (`di.ts`).                                |
| JSON             | System.Text.Json                      | built-in| camelCase-by-default output matches upstream JSON shapes.                    |
