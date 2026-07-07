using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Core.Events;
using OpenMercato.Core.Queue;
using OpenMercato.Modules.HealthCheck.Data;
using OpenMercato.Modules.HealthCheck.Validators;
using StackExchange.Redis;

namespace OpenMercato.Modules.HealthCheck.Api;

/// <summary>
/// HTTP routes for the health_check module
/// (upstream equivalent: api/get/health_check.ts, api/post/health_check/ping.ts).
/// </summary>
internal static class HealthCheckEndpoints
{
    public static void Map(IEndpointRouteBuilder routes)
    {
        // GET /api/health_check — readiness check with real DB + Redis pings.
        routes.MapGet("/api/health_check",
            async (AppDbContext db, IConnectionMultiplexer redis, CancellationToken ct) =>
            {
                var databaseOk = false;
                var redisOk = false;

                try
                {
                    // Query the module's own table so the check also proves migrations ran.
                    _ = await db.Set<HealthPing>().CountAsync(ct);
                    databaseOk = true;
                }
                catch
                {
                    // reported as checks.database = false
                }

                try
                {
                    await redis.GetDatabase().PingAsync();
                    redisOk = true;
                }
                catch
                {
                    // reported as checks.redis = false
                }

                var healthy = databaseOk && redisOk;
                var body = new
                {
                    status = healthy ? "ok" : "degraded",
                    module = "health_check",
                    checks = new { database = databaseOk, redis = redisOk },
                };
                return healthy ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
            });

        // POST /api/health_check/ping — validated write path exercising the whole
        // vertical: FluentValidation -> EF Core insert -> queue job -> event.
        routes.MapPost("/api/health_check/ping",
            async (PingRequest request, AppDbContext db, IJobQueue queue, IEventBus events, CancellationToken ct) =>
            {
                var validation = new PingRequestValidator().Validate(request);
                if (!validation.IsValid)
                {
                    return Results.BadRequest(new
                    {
                        error = "validation_failed",
                        details = validation.Errors
                            .Select(e => new { field = e.PropertyName, message = e.ErrorMessage }),
                    });
                }

                var ping = new HealthPing
                {
                    Id = Guid.NewGuid(),
                    Source = request.Source!,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                db.Set<HealthPing>().Add(ping);
                await db.SaveChangesAsync(ct);

                await queue.EnqueueAsync("health_check", "ping", new { pingId = ping.Id, source = ping.Source }, ct);
                await events.PublishAsync("health_check.pinged", new { pingId = ping.Id }, ct);

                return Results.Created(
                    $"/api/health_check/ping/{ping.Id}",
                    new { id = ping.Id, source = ping.Source, createdAt = ping.CreatedAt });
            });
    }
}
