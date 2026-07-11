using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

/// <summary>Company profile keys eligible for the <c>profile:{}</c> unwrap (payload.ts).</summary>
internal static class CompanyKeys
{
    public static readonly string[] Profile =
        { "legalName", "brandName", "domain", "websiteUrl", "industry", "sizeBucket", "annualRevenue" };
}

/// <summary>
/// <c>customers.companies.create</c> — inserts <c>customer_entities</c> (kind='company') + the
/// <c>customer_companies</c> satellite, syncs tags, and persists cf under
/// <c>customers:customer_company_profile</c>. Returns <c>{ entityId, companyId }</c>. Undoable.
/// </summary>
public sealed class CreateCompanyCommand
    : ICommand<CompanyCreateInput, CompanyResult>,
      ICommandLogMetadataBuilder<CompanyCreateInput, CompanyResult>,
      IUndoableCommand
{
    public string CommandId => "customers.companies.create";

    public async Task<CompanyResult> ExecuteAsync(CompanyCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        var now = DateTimeOffset.UtcNow;
        var displayName = J.Str(body, "displayName")?.Trim() ?? string.Empty;

        var entity = new CustomerEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            Kind = "company",
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now,
        };
        CustomerWriteHelpers.ApplyBaseCreate(entity, body);
        db.Set<CustomerEntity>().Add(entity);
        // Persist base customer_entities before satellite profile (DB FK ordering; EF has no relationship).
        await db.SaveChangesAsync();

        var profile = new CustomerCompanyProfile
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            EntityId = entity.Id,
            LegalName = J.Str(body, "legalName"),
            BrandName = J.Str(body, "brandName"),
            Domain = J.Str(body, "domain"),
            WebsiteUrl = J.Str(body, "websiteUrl"),
            Industry = J.Str(body, "industry"),
            SizeBucket = J.Str(body, "sizeBucket"),
            AnnualRevenue = J.Decimal(body, "annualRevenue"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<CustomerCompanyProfile>().Add(profile);

        await CustomerWriteHelpers.SyncTagsAsync(db, body, entity.Id, input.OrganizationId, input.TenantId);
        await db.SaveChangesAsync();
        await CustomerWriteHelpers.PersistCustomFieldsAsync(services, CustomerWriteHelpers.CompanyEntityType, entity.Id, body, ctx);

        return new CompanyResult(entity.Id.ToString(), profile.Id.ToString());
    }

    public CommandLogMetadata BuildLog(CompanyCreateInput input, CompanyResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create company",
        ResourceKind = "customers.company",
        ResourceId = result.EntityId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var e = await db.Set<CustomerEntity>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!));
        if (e is not null) e.DeletedAt = DateTimeOffset.UtcNow;
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var e = await db.Set<CustomerEntity>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!));
        if (e is not null) e.DeletedAt = null;
    }
}

/// <summary><c>customers.companies.update</c> — base + company profile fields (with unwrap), cf, optimistic lock.</summary>
public sealed class UpdateCompanyCommand
    : ICommand<CompanyUpdateInput, CompanyResult>,
      ICommandLogMetadataBuilder<CompanyUpdateInput, CompanyResult>,
      IUndoableCommand
{
    public string CommandId => "customers.companies.update";
    private CustomerEntitySnapshot? _before;
    private CustomerEntitySnapshot? _after;

    public async Task<CompanyResult> ExecuteAsync(CompanyUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var entity = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e =>
            e.Id == input.Id && e.Kind == "company" && e.DeletedAt == null);
        if (entity is null) throw CommandHttpException.NotFound("Company not found");
        if (ctx.TenantId is { } t && entity.TenantId != t) throw CommandHttpException.NotFound("Company not found");

        OptimisticLock.Enforce("customers.company", entity.Id.ToString(), entity.UpdatedAt.UtcDateTime, ctx);
        var profile = await db.Set<CustomerCompanyProfile>().FirstOrDefaultAsync(p => p.EntityId == entity.Id);
        _before = Snapshots.Of(entity);

        var fields = CustomerWriteHelpers.NormalizeProfile(input.Body, CompanyKeys.Profile);
        CustomerWriteHelpers.ApplyBaseUpdate(entity, fields);

        if (profile is not null)
        {
            if (fields.TryGetValue("legalName", out var v1)) profile.LegalName = CustomerWriteHelpers.AsString(v1);
            if (fields.TryGetValue("brandName", out var v2)) profile.BrandName = CustomerWriteHelpers.AsString(v2);
            if (fields.TryGetValue("domain", out var v3)) profile.Domain = CustomerWriteHelpers.AsString(v3);
            if (fields.TryGetValue("websiteUrl", out var v4)) profile.WebsiteUrl = CustomerWriteHelpers.AsString(v4);
            if (fields.TryGetValue("industry", out var v5)) profile.Industry = CustomerWriteHelpers.AsString(v5);
            if (fields.TryGetValue("sizeBucket", out var v6)) profile.SizeBucket = CustomerWriteHelpers.AsString(v6);
            if (fields.TryGetValue("annualRevenue", out var v7)) profile.AnnualRevenue = CustomerWriteHelpers.AsDecimal(v7);
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await CustomerWriteHelpers.PersistCustomFieldsAsync(services, CustomerWriteHelpers.CompanyEntityType, entity.Id, input.Body, ctx);
        _after = Snapshots.Of(entity);

        return new CompanyResult(entity.Id.ToString(), profile?.Id.ToString(), entity.UpdatedAt);
    }

    public CommandLogMetadata BuildLog(CompanyUpdateInput input, CompanyResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update company",
        ResourceKind = "customers.company",
        ResourceId = result.EntityId,
        TenantId = _before?.TenantId,
        OrganizationId = _before?.OrganizationId,
        SnapshotBefore = _before,
        SnapshotAfter = _after,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var snap = log.GetSnapshotBefore<CustomerEntitySnapshot>();
        if (snap is null) return;
        var e = await db.Set<CustomerEntity>().FirstOrDefaultAsync(x => x.Id == snap.Id);
        if (e is not null) Snapshots.Apply(e, snap);
    }

    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => Task.CompletedTask;
}

/// <summary><c>customers.companies.delete</c> — soft-deletes the base row. Undoable.</summary>
public sealed class DeleteCompanyCommand
    : ICommand<CompanyDeleteInput, CompanyResult>,
      ICommandLogMetadataBuilder<CompanyDeleteInput, CompanyResult>,
      IUndoableCommand
{
    public string CommandId => "customers.companies.delete";
    private CustomerEntitySnapshot? _before;

    public async Task<CompanyResult> ExecuteAsync(CompanyDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var entity = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e =>
            e.Id == input.Id && e.Kind == "company" && e.DeletedAt == null);
        if (entity is null) throw CommandHttpException.NotFound("Company not found");
        if (ctx.TenantId is { } t && entity.TenantId != t) throw CommandHttpException.NotFound("Company not found");
        _before = Snapshots.Of(entity);
        entity.DeletedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        return new CompanyResult(entity.Id.ToString(), null);
    }

    public CommandLogMetadata BuildLog(CompanyDeleteInput input, CompanyResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete company",
        ResourceKind = "customers.company",
        ResourceId = result.EntityId,
        TenantId = _before?.TenantId,
        OrganizationId = _before?.OrganizationId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var e = await db.Set<CustomerEntity>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!));
        if (e is not null) { e.DeletedAt = null; e.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var e = await db.Set<CustomerEntity>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!));
        if (e is not null) { e.DeletedAt = DateTimeOffset.UtcNow; e.UpdatedAt = DateTimeOffset.UtcNow; }
    }
}
