using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Crud;
using OpenMercato.Modules.Currencies.Commands;
using OpenMercato.Modules.Currencies.Data;
using OpenMercato.Modules.Currencies.Services;

namespace OpenMercato.Modules.Currencies.Api;

/// <summary>
/// Currencies + exchange-rates HTTP surface — the .NET port of currencies/api/*. The two admin CRUD
/// resources go through <see cref="CrudRoute.Map{TEntity}"/> (the makeCrudRoute equivalent): reads via
/// the generic factory GET, writes dispatched to the CommandBus. Adds the two read-only helpers the
/// upstream module ships: <c>/api/currencies/options</c> (select options) and a ConvertToBase endpoint.
/// </summary>
public static class CurrenciesRoutes
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder routes)
    {
        CrudRoute.Map(routes, CurrencyConfig());
        CrudRoute.Map(routes, ExchangeRateConfig());
        MapOptions(routes);
        MapConvert(routes);
        MapFetchConfigs(routes);
    }

    // ---- /api/currencies ----------------------------------------------------------------------

    private static CrudConfig<Currency> CurrencyConfig() => new()
    {
        // Upstream mounts each resource under /api/<module>/<resource> — the currency CRUD lives at
        // /api/currencies/currencies (NOT /api/currencies). This also matches the testbench proxy's
        // /api/currencies/* matcher. (Caught by OM integration test TC-CUR-001.)
        BasePath = "currencies/currencies",
        EntityType = "currencies:currency",
        ResourceKind = "currencies.currency",
        DefaultSortField = "code",
        ListFeatures = new[] { "currencies.view" },
        CreateFeatures = new[] { "currencies.manage" },
        UpdateFeatures = new[] { "currencies.manage" },
        DeleteFeatures = new[] { "currencies.manage" },
        IdSelector = c => c.Id,
        DeletedAtSelector = c => c.DeletedAt,
        TenantIdSelector = c => c.TenantId,
        OrganizationIdSelector = c => c.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<Currency>, bool, IOrderedQueryable<Currency>>>
        {
            ["code"] = (q, d) => d ? q.OrderByDescending(c => c.Code) : q.OrderBy(c => c.Code),
            ["name"] = (q, d) => d ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name),
            ["createdAt"] = (q, d) => d ? q.OrderByDescending(c => c.CreatedAt) : q.OrderBy(c => c.CreatedAt),
            ["updatedAt"] = (q, d) => d ? q.OrderByDescending(c => c.UpdatedAt) : q.OrderBy(c => c.UpdatedAt),
        },
        ApplyFilters = (q, query, _) =>
        {
            if (query.Filters.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code))
                q = q.Where(c => c.Code == code);
            if (query.Filters.TryGetValue("isBase", out var isBase))
                q = q.Where(c => c.IsBase == (isBase == "true"));
            if (query.Filters.TryGetValue("isActive", out var isActive))
                q = q.Where(c => c.IsActive == (isActive == "true"));
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var term = query.Search.Trim().ToLowerInvariant();
                q = q.Where(c => c.Code.ToLower().Contains(term) || c.Name.ToLower().Contains(term) ||
                                 (c.Symbol != null && c.Symbol.ToLower().Contains(term)));
            }
            return q;
        },
        ProjectItem = c => new Dictionary<string, object?>
        {
            ["id"] = c.Id.ToString(),
            ["code"] = c.Code,
            ["name"] = c.Name,
            ["symbol"] = c.Symbol,
            ["decimalPlaces"] = c.DecimalPlaces,
            ["thousandsSeparator"] = c.ThousandsSeparator,
            ["decimalSeparator"] = c.DecimalSeparator,
            ["isBase"] = c.IsBase,
            ["isActive"] = c.IsActive,
            ["createdAt"] = c.CreatedAt.ToUniversalTime().ToString("o"),
            ["updatedAt"] = c.UpdatedAt.ToUniversalTime().ToString("o"),
            ["organizationId"] = c.OrganizationId.ToString(),
            ["tenantId"] = c.TenantId.ToString(),
        },
        CreatedEvent = "currencies.currency.created",
        UpdatedEvent = "currencies.currency.updated",
        DeletedEvent = "currencies.currency.deleted",
        ValidateCreate = CurrencyValidators.ValidateCurrencyCreate,
        ValidateUpdate = CurrencyValidators.ValidateCurrencyUpdate,
        CreateDispatch = async m =>
        {
            var b = m.Body;
            var input = new CurrencyCreateInput
            {
                OrganizationId = m.Ctx.OrganizationId ?? Guid.Empty,
                TenantId = m.Ctx.TenantId ?? Guid.Empty,
                Code = CurrencyValidators.GetString(b, "code") ?? string.Empty,
                Name = CurrencyValidators.GetString(b, "name") ?? string.Empty,
                Symbol = CurrencyValidators.GetString(b, "symbol"),
                DecimalPlaces = CurrencyValidators.TryGetInt(b, "decimalPlaces", out var dp) ? dp : null,
                ThousandsSeparator = CurrencyValidators.GetString(b, "thousandsSeparator"),
                DecimalSeparator = CurrencyValidators.GetString(b, "decimalSeparator"),
                IsBase = CurrencyValidators.GetBool(b, "isBase"),
                IsActive = CurrencyValidators.GetBool(b, "isActive"),
            };
            var r = await m.Bus.ExecuteWithLog<CurrencyCreateInput, CurrencyResult>("currencies.currencies.create", input, m.Ctx);
            return new CrudMutationOutcome(r.Result.CurrencyId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var b = m.Body;
            CurrencyValidators.TryGetGuid(b, "id", out var id);
            var input = new CurrencyUpdateInput
            {
                Id = id,
                Code = CurrencyValidators.HasProp(b, "code") ? CurrencyValidators.GetString(b, "code") : null,
                Name = CurrencyValidators.HasProp(b, "name") ? CurrencyValidators.GetString(b, "name") : null,
                Symbol = CurrencyValidators.GetString(b, "symbol"),
                SymbolSet = CurrencyValidators.HasProp(b, "symbol"),
                DecimalPlaces = CurrencyValidators.TryGetInt(b, "decimalPlaces", out var dp) ? dp : null,
                ThousandsSeparator = CurrencyValidators.GetString(b, "thousandsSeparator"),
                ThousandsSeparatorSet = CurrencyValidators.HasProp(b, "thousandsSeparator"),
                DecimalSeparator = CurrencyValidators.GetString(b, "decimalSeparator"),
                DecimalSeparatorSet = CurrencyValidators.HasProp(b, "decimalSeparator"),
                IsBase = CurrencyValidators.GetBool(b, "isBase"),
                IsActive = CurrencyValidators.GetBool(b, "isActive"),
            };
            var r = await m.Bus.ExecuteWithLog<CurrencyUpdateInput, CurrencyResult>("currencies.currencies.update", input, m.Ctx);
            return new CrudMutationOutcome(r.Result.CurrencyId, r.LogEntry);
        },
        DeleteDispatch = async m =>
        {
            var id = m.Query.TryGetValue("id", out var raw) && Guid.TryParse(raw, out var g) ? g : Guid.Empty;
            var input = new CurrencyDeleteInput
            {
                Id = id,
                OrganizationId = m.Ctx.OrganizationId ?? Guid.Empty,
                TenantId = m.Ctx.TenantId ?? Guid.Empty,
            };
            var r = await m.Bus.ExecuteWithLog<CurrencyDeleteInput, CurrencyResult>("currencies.currencies.delete", input, m.Ctx);
            return new CrudMutationOutcome(r.Result.CurrencyId, r.LogEntry);
        },
    };

    // ---- /api/exchange-rates ------------------------------------------------------------------

    private static CrudConfig<ExchangeRate> ExchangeRateConfig() => new()
    {
        BasePath = "currencies/exchange-rates",
        EntityType = "currencies:exchange_rate",
        ResourceKind = "currencies.exchange_rate",
        // PARITY-TODO: upstream default order is `date DESC`; the factory GET defaults to ASC when no
        // sortDir is supplied. Explicit ?sortDir=desc reproduces the upstream default.
        DefaultSortField = "date",
        ListFeatures = new[] { "currencies.rates.view" },
        CreateFeatures = new[] { "currencies.rates.manage" },
        UpdateFeatures = new[] { "currencies.rates.manage" },
        DeleteFeatures = new[] { "currencies.rates.manage" },
        IdSelector = r => r.Id,
        DeletedAtSelector = r => r.DeletedAt,
        TenantIdSelector = r => r.TenantId,
        OrganizationIdSelector = r => r.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<ExchangeRate>, bool, IOrderedQueryable<ExchangeRate>>>
        {
            ["fromCurrencyCode"] = (q, d) => d ? q.OrderByDescending(r => r.FromCurrencyCode) : q.OrderBy(r => r.FromCurrencyCode),
            ["toCurrencyCode"] = (q, d) => d ? q.OrderByDescending(r => r.ToCurrencyCode) : q.OrderBy(r => r.ToCurrencyCode),
            ["date"] = (q, d) => d ? q.OrderByDescending(r => r.Date) : q.OrderBy(r => r.Date),
            ["createdAt"] = (q, d) => d ? q.OrderByDescending(r => r.CreatedAt) : q.OrderBy(r => r.CreatedAt),
            ["updatedAt"] = (q, d) => d ? q.OrderByDescending(r => r.UpdatedAt) : q.OrderBy(r => r.UpdatedAt),
        },
        ApplyFilters = (q, query, _) =>
        {
            if (query.Filters.TryGetValue("fromCurrencyCode", out var from) && !string.IsNullOrWhiteSpace(from))
                q = q.Where(r => r.FromCurrencyCode == from);
            if (query.Filters.TryGetValue("toCurrencyCode", out var to) && !string.IsNullOrWhiteSpace(to))
                q = q.Where(r => r.ToCurrencyCode == to);
            if (query.Filters.TryGetValue("source", out var source) && !string.IsNullOrWhiteSpace(source))
                q = q.Where(r => r.Source == source);
            if (query.Filters.TryGetValue("type", out var type) && !string.IsNullOrWhiteSpace(type))
                q = q.Where(r => r.Type == type);
            if (query.Filters.TryGetValue("isActive", out var isActive))
                q = q.Where(r => r.IsActive == (isActive == "true"));
            return q;
        },
        ProjectItem = r => new Dictionary<string, object?>
        {
            ["id"] = r.Id.ToString(),
            ["fromCurrencyCode"] = r.FromCurrencyCode,
            ["toCurrencyCode"] = r.ToCurrencyCode,
            ["rate"] = r.Rate.ToString(CultureInfo.InvariantCulture),
            ["date"] = r.Date.ToUniversalTime().ToString("o"),
            ["source"] = r.Source,
            ["type"] = r.Type,
            ["isActive"] = r.IsActive,
            ["createdAt"] = r.CreatedAt.ToUniversalTime().ToString("o"),
            ["updatedAt"] = r.UpdatedAt.ToUniversalTime().ToString("o"),
            ["organizationId"] = r.OrganizationId.ToString(),
            ["tenantId"] = r.TenantId.ToString(),
        },
        CreatedEvent = "currencies.exchange_rate.created",
        UpdatedEvent = "currencies.exchange_rate.updated",
        DeletedEvent = "currencies.exchange_rate.deleted",
        ValidateCreate = CurrencyValidators.ValidateExchangeRateCreate,
        ValidateUpdate = CurrencyValidators.ValidateExchangeRateUpdate,
        CreateDispatch = async m =>
        {
            var b = m.Body;
            CurrencyValidators.TryParseDate(b, "date", out var date);
            var input = new ExchangeRateCreateInput
            {
                OrganizationId = m.Ctx.OrganizationId ?? Guid.Empty,
                TenantId = m.Ctx.TenantId ?? Guid.Empty,
                FromCurrencyCode = CurrencyValidators.GetString(b, "fromCurrencyCode") ?? string.Empty,
                ToCurrencyCode = CurrencyValidators.GetString(b, "toCurrencyCode") ?? string.Empty,
                Rate = CurrencyValidators.GetString(b, "rate") ?? string.Empty,
                Date = date,
                Source = CurrencyValidators.GetString(b, "source") ?? string.Empty,
                Type = CurrencyValidators.GetString(b, "type"),
                IsActive = CurrencyValidators.GetBool(b, "isActive"),
            };
            var r = await m.Bus.ExecuteWithLog<ExchangeRateCreateInput, ExchangeRateResult>("currencies.exchange_rates.create", input, m.Ctx);
            return new CrudMutationOutcome(r.Result.ExchangeRateId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var b = m.Body;
            CurrencyValidators.TryGetGuid(b, "id", out var id);
            DateTimeOffset? date = CurrencyValidators.TryParseDate(b, "date", out var d) ? d : null;
            var input = new ExchangeRateUpdateInput
            {
                Id = id,
                FromCurrencyCode = CurrencyValidators.HasProp(b, "fromCurrencyCode") ? CurrencyValidators.GetString(b, "fromCurrencyCode") : null,
                ToCurrencyCode = CurrencyValidators.HasProp(b, "toCurrencyCode") ? CurrencyValidators.GetString(b, "toCurrencyCode") : null,
                Rate = CurrencyValidators.HasProp(b, "rate") ? CurrencyValidators.GetString(b, "rate") : null,
                Date = date,
                Source = CurrencyValidators.HasProp(b, "source") ? CurrencyValidators.GetString(b, "source") : null,
                Type = CurrencyValidators.GetString(b, "type"),
                TypeSet = CurrencyValidators.HasProp(b, "type"),
                IsActive = CurrencyValidators.GetBool(b, "isActive"),
            };
            var r = await m.Bus.ExecuteWithLog<ExchangeRateUpdateInput, ExchangeRateResult>("currencies.exchange_rates.update", input, m.Ctx);
            return new CrudMutationOutcome(r.Result.ExchangeRateId, r.LogEntry);
        },
        DeleteDispatch = async m =>
        {
            var id = m.Query.TryGetValue("id", out var raw) && Guid.TryParse(raw, out var g) ? g : Guid.Empty;
            var input = new ExchangeRateDeleteInput
            {
                Id = id,
                OrganizationId = m.Ctx.OrganizationId ?? Guid.Empty,
                TenantId = m.Ctx.TenantId ?? Guid.Empty,
            };
            var r = await m.Bus.ExecuteWithLog<ExchangeRateDeleteInput, ExchangeRateResult>("currencies.exchange_rates.delete", input, m.Ctx);
            return new CrudMutationOutcome(r.Result.ExchangeRateId, r.LogEntry);
        },
    };

    // ---- /api/currencies/options (select options; port of api/currencies/options/route.ts) ----

    private static void MapOptions(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/currencies/options", (Func<HttpContext, Task<IResult>>)(async http =>
        {
            var (ctx, denied) = await AuthorizeAsync(http, new[] { "currencies.view" });
            if (denied is not null) return denied;

            var db = http.RequestServices.GetRequiredService<Core.Data.AppDbContext>();
            var q = http.Request.Query;
            var term = (First(q["q"]) ?? First(q["query"]) ?? First(q["search"]) ?? string.Empty).Trim().ToLowerInvariant();
            var includeInactive = q["includeInactive"].ToString() == "true";
            var limit = int.TryParse(q["limit"].ToString(), out var l) ? Math.Clamp(l, 1, 100) : 50;

            var query = db.Set<Currency>().AsNoTracking().Where(c => c.TenantId == ctx!.TenantId && c.DeletedAt == null);
            if (ctx!.OrganizationIds is { Count: > 0 } orgIds)
                query = query.Where(c => orgIds.Contains(c.OrganizationId));
            if (!includeInactive) query = query.Where(c => c.IsActive);
            if (term.Length > 0)
                query = query.Where(c => c.Code.ToLower().Contains(term) || c.Name.ToLower().Contains(term));

            var rows = await query.OrderBy(c => c.Code).Take(limit).ToListAsync();
            var items = rows.Select(c => new { value = c.Code, label = $"{c.Code} - {c.Name}" });
            return Results.Json(new { items }, Web, statusCode: 200);
        }));
    }

    // ---- /api/currencies/convert (ConvertToBase-style helper for consumers) -------------------

    private static void MapConvert(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/currencies/convert", (Func<HttpContext, Task<IResult>>)(async http =>
        {
            var (ctx, denied) = await AuthorizeAsync(http, new[] { "currencies.rates.view" });
            if (denied is not null) return denied;
            if (ctx!.TenantId is not { } tenantId || ctx.OrganizationId is not { } orgId)
                return Results.Json(new { error = "Forbidden" }, Web, statusCode: 403);

            var q = http.Request.Query;
            var from = (First(q["from"]) ?? string.Empty).Trim().ToUpperInvariant();
            if (!decimal.TryParse(First(q["amount"]), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                return Results.Json(new { error = "amount and from are required" }, Web, statusCode: 400);
            var date = DateTimeOffset.TryParse(First(q["date"]), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal, out var d) ? d : DateTimeOffset.UtcNow;

            var service = http.RequestServices.GetRequiredService<IExchangeRateService>();
            var result = await service.ConvertToBaseAsync(amount, from, date, new CurrencyScope(tenantId, orgId));
            return Results.Json(new
            {
                amount = result.Amount,
                baseCurrencyCode = result.BaseCurrencyCode,
                rate = result.Rate,
                converted = result.Converted,
            }, Web, statusCode: 200);
        }));
    }

    // ---- /api/currencies/fetch-configs (port of api/fetch-configs/route.ts) -------------------
    // Bespoke (non-CrudRoute) shape: GET → {configs:[...]}, POST/PUT → {config}, DELETE → {success:true}.

    private static readonly string[] FetchProviders = { "NBP", "Raiffeisen Bank Polska", "Custom" };
    private static readonly System.Text.RegularExpressions.Regex SyncTimeRe =
        new(@"^([01]\d|2[0-3]):([0-5]\d)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void MapFetchConfigs(IEndpointRouteBuilder routes)
    {
        // GET — list configs for the scope. Superadmin (no org) may read across the tenant.
        routes.MapGet("/api/currencies/fetch-configs", (Func<HttpContext, Task<IResult>>)(async http =>
        {
            var (ctx, denied) = await AuthorizeAsync(http, new[] { "currencies.fetch.view" });
            if (denied is not null) return denied;
            if (ctx!.TenantId is not { } tenantId) return Results.Json(new { error = "Unauthorized" }, Web, statusCode: 401);

            var db = http.RequestServices.GetRequiredService<Core.Data.AppDbContext>();
            var query = db.Set<CurrencyFetchConfig>().AsNoTracking().Where(c => c.TenantId == tenantId);
            if (ctx.OrganizationId is { } orgId) query = query.Where(c => c.OrganizationId == orgId);
            var rows = await query.OrderBy(c => c.Provider).ToListAsync();
            return Results.Json(new { configs = rows.Select(ProjectFetchConfig) }, Web, statusCode: 200);
        }));

        // POST — create; duplicate provider in scope → 400.
        routes.MapPost("/api/currencies/fetch-configs", (Func<HttpContext, Task<IResult>>)(async http =>
        {
            var (ctx, denied) = await AuthorizeAsync(http, new[] { "currencies.fetch.view" });
            if (denied is not null) return denied;
            if (ctx!.TenantId is not { } tenantId || ctx.OrganizationId is not { } orgId)
                return Results.Json(new { error = "Unauthorized" }, Web, statusCode: 401);

            var body = await ReadBodyAsync(http);
            if (body is null) return Results.Json(new { error = "Invalid JSON" }, Web, statusCode: 400);

            var provider = GetStr(body.Value, "provider");
            if (provider is null || Array.IndexOf(FetchProviders, provider) < 0)
                return Results.Json(new { error = "Invalid provider" }, Web, statusCode: 400);
            var syncTime = GetStr(body.Value, "syncTime");
            if (syncTime is not null && !SyncTimeRe.IsMatch(syncTime))
                return Results.Json(new { error = "Invalid time format. Use HH:MM" }, Web, statusCode: 400);

            var db = http.RequestServices.GetRequiredService<Core.Data.AppDbContext>();
            var dup = await db.Set<CurrencyFetchConfig>()
                .AnyAsync(c => c.TenantId == tenantId && c.OrganizationId == orgId && c.Provider == provider);
            if (dup) return Results.Json(new { error = $"Provider {provider} already configured" }, Web, statusCode: 400);

            var now = DateTimeOffset.UtcNow;
            var entity = new CurrencyFetchConfig
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                TenantId = tenantId,
                Provider = provider,
                IsEnabled = GetBool(body.Value, "isEnabled") ?? false,
                SyncTime = syncTime,
                Config = GetRawJson(body.Value, "config"),
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Set<CurrencyFetchConfig>().Add(entity);
            await db.SaveChangesAsync();
            return Results.Json(new { config = ProjectFetchConfig(entity) }, Web, statusCode: 201);
        }));

        // PUT — update isEnabled/syncTime/config by id.
        routes.MapPut("/api/currencies/fetch-configs", (Func<HttpContext, Task<IResult>>)(async http =>
        {
            var (ctx, denied) = await AuthorizeAsync(http, new[] { "currencies.fetch.view" });
            if (denied is not null) return denied;
            if (ctx!.TenantId is not { } tenantId || ctx.OrganizationId is not { } orgId)
                return Results.Json(new { error = "Unauthorized" }, Web, statusCode: 401);

            var body = await ReadBodyAsync(http);
            if (body is null) return Results.Json(new { error = "Invalid JSON" }, Web, statusCode: 400);
            if (GetStr(body.Value, "id") is not { } idStr || !Guid.TryParse(idStr, out var id))
                return Results.Json(new { error = "ID required" }, Web, statusCode: 400);

            var db = http.RequestServices.GetRequiredService<Core.Data.AppDbContext>();
            var entity = await db.Set<CurrencyFetchConfig>()
                .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId && c.OrganizationId == orgId);
            if (entity is null) return Results.Json(new { error = "Fetch config not found" }, Web, statusCode: 400);

            if (body.Value.TryGetProperty("isEnabled", out _)) entity.IsEnabled = GetBool(body.Value, "isEnabled") ?? entity.IsEnabled;
            if (body.Value.TryGetProperty("syncTime", out _))
            {
                var syncTime = GetStr(body.Value, "syncTime");
                if (syncTime is not null && !SyncTimeRe.IsMatch(syncTime))
                    return Results.Json(new { error = "Invalid time format. Use HH:MM" }, Web, statusCode: 400);
                entity.SyncTime = syncTime;
            }
            if (body.Value.TryGetProperty("config", out _)) entity.Config = GetRawJson(body.Value, "config");
            entity.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Json(new { config = ProjectFetchConfig(entity) }, Web, statusCode: 200);
        }));

        // DELETE ?id= — hard delete, returns {success:true}.
        routes.MapDelete("/api/currencies/fetch-configs", (Func<HttpContext, Task<IResult>>)(async http =>
        {
            var (ctx, denied) = await AuthorizeAsync(http, new[] { "currencies.fetch.view" });
            if (denied is not null) return denied;
            if (ctx!.TenantId is not { } tenantId || ctx.OrganizationId is not { } orgId)
                return Results.Json(new { error = "Unauthorized" }, Web, statusCode: 401);

            if (First(http.Request.Query["id"]) is not { } idStr || !Guid.TryParse(idStr, out var id))
                return Results.Json(new { error = "ID required" }, Web, statusCode: 400);

            var db = http.RequestServices.GetRequiredService<Core.Data.AppDbContext>();
            var entity = await db.Set<CurrencyFetchConfig>()
                .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId && c.OrganizationId == orgId);
            if (entity is null) return Results.Json(new { error = "Fetch config not found" }, Web, statusCode: 400);
            db.Set<CurrencyFetchConfig>().Remove(entity);
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, Web, statusCode: 200);
        }));
    }

    private static Dictionary<string, object?> ProjectFetchConfig(CurrencyFetchConfig c) => new()
    {
        ["id"] = c.Id.ToString(),
        ["organizationId"] = c.OrganizationId.ToString(),
        ["tenantId"] = c.TenantId.ToString(),
        ["provider"] = c.Provider,
        ["isEnabled"] = c.IsEnabled,
        ["syncTime"] = c.SyncTime,
        ["lastSyncAt"] = c.LastSyncAt?.ToUniversalTime().ToString("o"),
        ["lastSyncStatus"] = c.LastSyncStatus,
        ["lastSyncMessage"] = c.LastSyncMessage,
        ["lastSyncCount"] = c.LastSyncCount,
        ["config"] = c.Config is null ? null : JsonSerializer.Deserialize<JsonElement>(c.Config),
        ["createdAt"] = c.CreatedAt.ToUniversalTime().ToString("o"),
        ["updatedAt"] = c.UpdatedAt.ToUniversalTime().ToString("o"),
    };

    private static async Task<JsonElement?> ReadBodyAsync(HttpContext http)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static string? GetStr(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static bool? GetBool(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

    private static string? GetRawJson(JsonElement o, string key) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(key, out var v) &&
        v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v.GetRawText() : null;

    // ---- Auth bridge (mirrors CrudRoute.AuthorizeAsync for the non-factory endpoints) ---------

    private static async Task<(Core.Commands.CommandContext? Ctx, IResult? Denied)> AuthorizeAsync(HttpContext http, string[] features)
    {
        var requestContext = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var ctx = await requestContext.ResolveAsync(http);
        if (ctx is null) return (null, Results.Json(new { error = "Unauthorized" }, Web, statusCode: 401));
        if (!await requestContext.HasAllFeaturesAsync(ctx, features))
            return (null, Results.Json(new { error = "Forbidden", requiredFeatures = features }, Web, statusCode: 403));
        return (ctx, null);
    }

    private static string? First(Microsoft.Extensions.Primitives.StringValues v)
    {
        var s = v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
