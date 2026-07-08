using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

/// <summary>Person profile keys eligible for the <c>profile:{}</c> unwrap (payload.ts).</summary>
internal static class PersonKeys
{
    public static readonly string[] Profile =
    {
        "firstName", "lastName", "preferredName", "jobTitle", "department",
        "seniority", "timezone", "linkedInUrl", "twitterUrl", "companyEntityId",
    };
}

/// <summary>
/// <c>customers.people.create</c> — inserts the polymorphic base row (<c>customer_entities</c>,
/// kind='person') AND the 1:1 satellite profile (<c>customer_people</c>) atomically, syncs free-pool
/// tags, and persists <c>cf_*</c> custom fields under <c>customers:customer_person_profile</c>. Undoable
/// (undo soft-deletes the base row). Returns <c>{ entityId, personId }</c> (upstream 201 shape).
/// </summary>
public sealed class CreatePersonCommand
    : ICommand<PersonCreateInput, PersonResult>,
      ICommandLogMetadataBuilder<PersonCreateInput, PersonResult>,
      IUndoableCommand
{
    public string CommandId => "customers.people.create";
    private Guid _entityId;

    public async Task<PersonResult> ExecuteAsync(PersonCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        var now = DateTimeOffset.UtcNow;

        var firstName = J.Str(body, "firstName")?.Trim() ?? string.Empty;
        var lastName = J.Str(body, "lastName")?.Trim() ?? string.Empty;
        var displayName = J.Str(body, "displayName")?.Trim();
        if (string.IsNullOrEmpty(displayName))
            displayName = string.Join(' ', new[] { firstName, lastName }.Where(s => s.Length > 0));
        if (string.IsNullOrEmpty(displayName)) displayName = firstName.Length > 0 ? firstName : lastName;

        var entity = new CustomerEntity
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            Kind = "person",
            DisplayName = displayName,
            CreatedAt = now,
            UpdatedAt = now,
        };
        CustomerWriteHelpers.ApplyBaseCreate(entity, body);
        db.Set<CustomerEntity>().Add(entity);
        // Persist the base customer_entities row before its satellite profile: ConfigureModel maps
        // columns only (no FK relationships), so EF can't order inserts to satisfy the DB FK.
        await db.SaveChangesAsync();

        var profile = new CustomerPersonProfile
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            EntityId = entity.Id,
            FirstName = firstName.Length > 0 ? firstName : null,
            LastName = lastName.Length > 0 ? lastName : null,
            PreferredName = J.Str(body, "preferredName"),
            JobTitle = J.Str(body, "jobTitle"),
            Department = J.Str(body, "department"),
            Seniority = J.Str(body, "seniority"),
            Timezone = J.Str(body, "timezone"),
            LinkedInUrl = J.Str(body, "linkedInUrl"),
            TwitterUrl = J.Str(body, "twitterUrl"),
            CompanyEntityId = J.GuidOf(body, "companyEntityId"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<CustomerPersonProfile>().Add(profile);

        await CustomerWriteHelpers.SyncTagsAsync(db, body, entity.Id, input.OrganizationId, input.TenantId);
        await db.SaveChangesAsync();
        await CustomerWriteHelpers.PersistCustomFieldsAsync(services, CustomerWriteHelpers.PersonEntityType, entity.Id, body, ctx);

        _entityId = entity.Id;
        return new PersonResult(entity.Id.ToString(), profile.Id.ToString());
    }

    public CommandLogMetadata BuildLog(PersonCreateInput input, PersonResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create person",
        ResourceKind = "customers.person",
        ResourceId = result.EntityId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var e = await db.Set<CustomerEntity>().FirstOrDefaultAsync(x => x.Id == id);
        if (e is not null) e.DeletedAt = DateTimeOffset.UtcNow;
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var e = await db.Set<CustomerEntity>().FirstOrDefaultAsync(x => x.Id == id);
        if (e is not null) e.DeletedAt = null;
    }
}

/// <summary>
/// <c>customers.people.update</c> — applies base + profile fields (with <c>profile:{}</c> unwrap),
/// persists cf changes, and re-stamps <c>updated_at</c>; optimistic-locked. Undoable.
/// </summary>
public sealed class UpdatePersonCommand
    : ICommand<PersonUpdateInput, PersonResult>,
      ICommandLogMetadataBuilder<PersonUpdateInput, PersonResult>,
      IUndoableCommand
{
    public string CommandId => "customers.people.update";
    private CustomerEntitySnapshot? _before;

    public async Task<PersonResult> ExecuteAsync(PersonUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var entity = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e =>
            e.Id == input.Id && e.Kind == "person" && e.DeletedAt == null);
        if (entity is null) throw CommandHttpException.NotFound("Person not found");
        if (ctx.TenantId is { } t && entity.TenantId != t) throw CommandHttpException.NotFound("Person not found");

        OptimisticLock.Enforce("customers.person", entity.Id.ToString(), entity.UpdatedAt.UtcDateTime, ctx);

        var profile = await db.Set<CustomerPersonProfile>().FirstOrDefaultAsync(p => p.EntityId == entity.Id);
        _before = Snapshots.Of(entity);

        var fields = CustomerWriteHelpers.NormalizeProfile(input.Body, PersonKeys.Profile);
        CustomerWriteHelpers.ApplyBaseUpdate(entity, fields);

        if (profile is not null)
        {
            if (fields.TryGetValue("firstName", out var fn)) profile.FirstName = CustomerWriteHelpers.AsString(fn);
            if (fields.TryGetValue("lastName", out var ln)) profile.LastName = CustomerWriteHelpers.AsString(ln);
            if (fields.TryGetValue("preferredName", out var pn)) profile.PreferredName = CustomerWriteHelpers.AsString(pn);
            if (fields.TryGetValue("jobTitle", out var jt)) profile.JobTitle = CustomerWriteHelpers.AsString(jt);
            if (fields.TryGetValue("department", out var dp)) profile.Department = CustomerWriteHelpers.AsString(dp);
            if (fields.TryGetValue("seniority", out var sn)) profile.Seniority = CustomerWriteHelpers.AsString(sn);
            if (fields.TryGetValue("timezone", out var tz)) profile.Timezone = CustomerWriteHelpers.AsString(tz);
            if (fields.TryGetValue("linkedInUrl", out var li)) profile.LinkedInUrl = CustomerWriteHelpers.AsString(li);
            if (fields.TryGetValue("twitterUrl", out var tw)) profile.TwitterUrl = CustomerWriteHelpers.AsString(tw);
            if (fields.TryGetValue("companyEntityId", out var ce)) profile.CompanyEntityId = CustomerWriteHelpers.AsGuid(ce);
            profile.UpdatedAt = DateTimeOffset.UtcNow;
        }

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await CustomerWriteHelpers.PersistCustomFieldsAsync(services, CustomerWriteHelpers.PersonEntityType, entity.Id, input.Body, ctx);

        return new PersonResult(entity.Id.ToString(), profile?.Id.ToString(), entity.UpdatedAt);
    }

    public CommandLogMetadata BuildLog(PersonUpdateInput input, PersonResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update person",
        ResourceKind = "customers.person",
        ResourceId = result.EntityId,
        TenantId = _before?.TenantId,
        OrganizationId = _before?.OrganizationId,
        SnapshotBefore = _before,
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

/// <summary><c>customers.people.delete</c> — soft-deletes the base row. Undoable (restores).</summary>
public sealed class DeletePersonCommand
    : ICommand<PersonDeleteInput, PersonResult>,
      ICommandLogMetadataBuilder<PersonDeleteInput, PersonResult>,
      IUndoableCommand
{
    public string CommandId => "customers.people.delete";
    private CustomerEntitySnapshot? _before;

    public async Task<PersonResult> ExecuteAsync(PersonDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var entity = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e =>
            e.Id == input.Id && e.Kind == "person" && e.DeletedAt == null);
        if (entity is null) throw CommandHttpException.NotFound("Person not found");
        if (ctx.TenantId is { } t && entity.TenantId != t) throw CommandHttpException.NotFound("Person not found");
        _before = Snapshots.Of(entity);
        entity.DeletedAt = DateTimeOffset.UtcNow;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        return new PersonResult(entity.Id.ToString(), null);
    }

    public CommandLogMetadata BuildLog(PersonDeleteInput input, PersonResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete person",
        ResourceKind = "customers.person",
        ResourceId = result.EntityId,
        TenantId = _before?.TenantId,
        OrganizationId = _before?.OrganizationId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var e = await db.Set<CustomerEntity>().FirstOrDefaultAsync(x => x.Id == id);
        if (e is not null) { e.DeletedAt = null; e.UpdatedAt = DateTimeOffset.UtcNow; }
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(log.ResourceId!);
        var e = await db.Set<CustomerEntity>().FirstOrDefaultAsync(x => x.Id == id);
        if (e is not null) { e.DeletedAt = DateTimeOffset.UtcNow; e.UpdatedAt = DateTimeOffset.UtcNow; }
    }
}
