using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

/// <summary>Port of upstream <c>slugifyLabel</c> — lowercase, non-alnum → '-', collapsed and trimmed.</summary>
internal static class Slugify
{
    public static string Label(string input)
    {
        var lower = (input ?? string.Empty).Trim().ToLowerInvariant();
        var slug = Regex.Replace(lower, "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "label" : slug;
    }
}

// ---- Labels ----------------------------------------------------------------------------------

/// <summary><c>customers.labels.create</c> — per-user private label (UNIQUE user_id,tenant,org,slug). 409 on dup.</summary>
public sealed class CreateLabelCommand
    : ICommand<LabelCreateInput, LabelResult>, ICommandLogMetadataBuilder<LabelCreateInput, LabelResult>, IUndoableCommand
{
    public string CommandId => "customers.labels.create";
    private Guid? _org, _tenant;

    public async Task<LabelResult> ExecuteAsync(LabelCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        _org = input.OrganizationId; _tenant = input.TenantId;
        var dup = await db.Set<CustomerLabel>().AnyAsync(l =>
            l.UserId == input.UserId && l.TenantId == input.TenantId && l.OrganizationId == input.OrganizationId && l.Slug == input.Slug);
        if (dup) throw CommandHttpException.Conflict("A label with this slug already exists.");
        var now = DateTimeOffset.UtcNow;
        var label = new CustomerLabel
        {
            Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, TenantId = input.TenantId,
            UserId = input.UserId, Slug = input.Slug, Label = input.Label, CreatedAt = now, UpdatedAt = now,
        };
        db.Set<CustomerLabel>().Add(label);
        return new LabelResult(label.Id.ToString(), label.Slug, label.Label);
    }

    public CommandLogMetadata BuildLog(LabelCreateInput input, LabelResult result, CommandContext ctx) => new()
    { ActionLabel = "Create label", ResourceKind = "customers.label", ResourceId = result.Id, TenantId = _tenant, OrganizationId = _org };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    { var db = services.GetRequiredService<AppDbContext>(); var l = await db.Set<CustomerLabel>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!)); if (l is not null) db.Set<CustomerLabel>().Remove(l); }
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => Task.CompletedTask;
}

/// <summary><c>customers.labels.assign</c> — upsert a <c>customer_label_assignments</c> row (returns created flag → 201/200).</summary>
public sealed class AssignLabelCommand
    : ICommand<LabelAssignInput, LabelAssignResult>, ICommandLogMetadataBuilder<LabelAssignInput, LabelAssignResult>
{
    public string CommandId => "customers.labels.assign";
    private Guid? _org, _tenant;

    public async Task<LabelAssignResult> ExecuteAsync(LabelAssignInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        _org = input.OrganizationId; _tenant = input.TenantId;
        var existing = await db.Set<CustomerLabelAssignment>().FirstOrDefaultAsync(a => a.LabelId == input.LabelId && a.EntityId == input.EntityId);
        if (existing is not null) return new LabelAssignResult(existing.Id.ToString(), Created: false);
        var row = new CustomerLabelAssignment
        {
            Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, TenantId = input.TenantId,
            UserId = input.UserId, LabelId = input.LabelId, EntityId = input.EntityId, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Set<CustomerLabelAssignment>().Add(row);
        return new LabelAssignResult(row.Id.ToString(), Created: true);
    }

    public CommandLogMetadata BuildLog(LabelAssignInput input, LabelAssignResult result, CommandContext ctx) => new()
    { ActionLabel = "Assign label", ResourceKind = "customers.label_assignment", ResourceId = result.AssignmentId, TenantId = _tenant, OrganizationId = _org };
}

/// <summary><c>customers.labels.unassign</c> — remove a <c>customer_label_assignments</c> row (null when none).</summary>
public sealed class UnassignLabelCommand
    : ICommand<LabelAssignInput, LabelAssignResult>, ICommandLogMetadataBuilder<LabelAssignInput, LabelAssignResult>
{
    public string CommandId => "customers.labels.unassign";
    private Guid? _org, _tenant;

    public async Task<LabelAssignResult> ExecuteAsync(LabelAssignInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        _org = input.OrganizationId; _tenant = input.TenantId;
        var existing = await db.Set<CustomerLabelAssignment>().FirstOrDefaultAsync(a => a.LabelId == input.LabelId && a.EntityId == input.EntityId);
        if (existing is null) return new LabelAssignResult(null, Created: false);
        db.Set<CustomerLabelAssignment>().Remove(existing);
        return new LabelAssignResult(existing.Id.ToString(), Created: false);
    }

    public CommandLogMetadata BuildLog(LabelAssignInput input, LabelAssignResult result, CommandContext ctx) => new()
    { ActionLabel = "Unassign label", ResourceKind = "customers.label_assignment", ResourceId = result.AssignmentId, TenantId = _tenant, OrganizationId = _org };
}

// ---- Entity roles ----------------------------------------------------------------------------

/// <summary><c>customers.entityRoles.create</c> — assign a per-user role on an entity (partial UNIQUE). 409 on dup.</summary>
public sealed class CreateEntityRoleCommand
    : ICommand<EntityRoleCreateInput, EntityRoleResult>, ICommandLogMetadataBuilder<EntityRoleCreateInput, EntityRoleResult>, IUndoableCommand
{
    public string CommandId => "customers.entityRoles.create";
    private Guid? _org, _tenant;

    public async Task<EntityRoleResult> ExecuteAsync(EntityRoleCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        _org = input.OrganizationId; _tenant = input.TenantId;
        var dup = await db.Set<CustomerEntityRole>().AnyAsync(r =>
            r.EntityType == input.EntityType && r.EntityId == input.EntityId && r.RoleType == input.RoleType && r.DeletedAt == null);
        if (dup) throw CommandHttpException.Conflict("Role already assigned");
        var now = DateTimeOffset.UtcNow;
        var role = new CustomerEntityRole
        {
            Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, TenantId = input.TenantId,
            EntityType = input.EntityType, EntityId = input.EntityId, RoleType = input.RoleType, UserId = input.UserId,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Set<CustomerEntityRole>().Add(role);
        return new EntityRoleResult(role.Id.ToString());
    }

    public CommandLogMetadata BuildLog(EntityRoleCreateInput input, EntityRoleResult result, CommandContext ctx) => new()
    { ActionLabel = "Create entity role", ResourceKind = "customers.entity_role", ResourceId = result.RoleId, TenantId = _tenant, OrganizationId = _org };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    { var db = services.GetRequiredService<AppDbContext>(); var r = await db.Set<CustomerEntityRole>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!)); if (r is not null) r.DeletedAt = DateTimeOffset.UtcNow; }
    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    { var db = services.GetRequiredService<AppDbContext>(); var r = await db.Set<CustomerEntityRole>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!)); if (r is not null) r.DeletedAt = null; }
}

/// <summary><c>customers.entityRoles.update</c> — reassign the role to a different user. 404 when missing.</summary>
public sealed class UpdateEntityRoleCommand
    : ICommand<EntityRoleUpdateInput, EntityRoleResult>, ICommandLogMetadataBuilder<EntityRoleUpdateInput, EntityRoleResult>
{
    public string CommandId => "customers.entityRoles.update";
    private Guid? _org, _tenant;

    public async Task<EntityRoleResult> ExecuteAsync(EntityRoleUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var r = await db.Set<CustomerEntityRole>().FirstOrDefaultAsync(x => x.Id == input.Id && x.DeletedAt == null);
        if (r is null) throw CommandHttpException.NotFound("Role not found");
        _org = r.OrganizationId; _tenant = r.TenantId;
        r.UserId = input.UserId;
        r.UpdatedAt = DateTimeOffset.UtcNow;
        return new EntityRoleResult(r.Id.ToString());
    }

    public CommandLogMetadata BuildLog(EntityRoleUpdateInput input, EntityRoleResult result, CommandContext ctx) => new()
    { ActionLabel = "Update entity role", ResourceKind = "customers.entity_role", ResourceId = result.RoleId, TenantId = _tenant, OrganizationId = _org };
}

/// <summary><c>customers.entityRoles.delete</c> — soft-deletes a role assignment.</summary>
public sealed class DeleteEntityRoleCommand
    : ICommand<EntityRoleDeleteInput, EntityRoleResult>, ICommandLogMetadataBuilder<EntityRoleDeleteInput, EntityRoleResult>
{
    public string CommandId => "customers.entityRoles.delete";
    private Guid? _org, _tenant;

    public async Task<EntityRoleResult> ExecuteAsync(EntityRoleDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var r = await db.Set<CustomerEntityRole>().FirstOrDefaultAsync(x => x.Id == input.Id && x.DeletedAt == null);
        if (r is null) throw CommandHttpException.NotFound("Role not found");
        _org = r.OrganizationId; _tenant = r.TenantId;
        r.DeletedAt = DateTimeOffset.UtcNow;
        r.UpdatedAt = DateTimeOffset.UtcNow;
        return new EntityRoleResult(r.Id.ToString());
    }

    public CommandLogMetadata BuildLog(EntityRoleDeleteInput input, EntityRoleResult result, CommandContext ctx) => new()
    { ActionLabel = "Delete entity role", ResourceKind = "customers.entity_role", ResourceId = result.RoleId, TenantId = _tenant, OrganizationId = _org };
}

// ---- Person↔company links --------------------------------------------------------------------

/// <summary><c>customers.personCompanyLinks.create</c> — link a person to a company (partial UNIQUE, revives soft-deleted).</summary>
public sealed class CreatePersonCompanyLinkCommand
    : ICommand<PersonCompanyLinkCreateInput, PersonCompanyLinkResult>, ICommandLogMetadataBuilder<PersonCompanyLinkCreateInput, PersonCompanyLinkResult>
{
    public string CommandId => "customers.personCompanyLinks.create";
    private Guid? _org, _tenant;

    public async Task<PersonCompanyLinkResult> ExecuteAsync(PersonCompanyLinkCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        _org = input.OrganizationId; _tenant = input.TenantId;
        var now = DateTimeOffset.UtcNow;
        var link = await db.Set<CustomerPersonCompanyLink>().FirstOrDefaultAsync(l =>
            l.PersonEntityId == input.PersonEntityId && l.CompanyEntityId == input.CompanyEntityId);
        if (link is null)
        {
            link = new CustomerPersonCompanyLink
            {
                Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, TenantId = input.TenantId,
                PersonEntityId = input.PersonEntityId, CompanyEntityId = input.CompanyEntityId,
                IsPrimary = input.IsPrimary ?? false, CreatedAt = now, UpdatedAt = now,
            };
            db.Set<CustomerPersonCompanyLink>().Add(link);
        }
        else
        {
            link.DeletedAt = null;
            if (input.IsPrimary is { } p) link.IsPrimary = p;
            link.UpdatedAt = now;
        }
        var company = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e => e.Id == input.CompanyEntityId);
        return new PersonCompanyLinkResult(link.Id.ToString(), input.CompanyEntityId.ToString(), company?.DisplayName, link.IsPrimary);
    }

    public CommandLogMetadata BuildLog(PersonCompanyLinkCreateInput input, PersonCompanyLinkResult result, CommandContext ctx) => new()
    { ActionLabel = "Link person to company", ResourceKind = "customers.person_company_link", ResourceId = result.Id, TenantId = _tenant, OrganizationId = _org };
}

/// <summary><c>customers.personCompanyLinks.update</c> — toggle a link's primary flag.</summary>
public sealed class UpdatePersonCompanyLinkCommand
    : ICommand<PersonCompanyLinkUpdateInput, PersonCompanyLinkResult>, ICommandLogMetadataBuilder<PersonCompanyLinkUpdateInput, PersonCompanyLinkResult>
{
    public string CommandId => "customers.personCompanyLinks.update";
    private Guid? _org, _tenant;

    public async Task<PersonCompanyLinkResult> ExecuteAsync(PersonCompanyLinkUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var link = await db.Set<CustomerPersonCompanyLink>().FirstOrDefaultAsync(l => l.Id == input.LinkId && l.DeletedAt == null);
        if (link is null) throw CommandHttpException.NotFound("Person-company link not found");
        _org = link.OrganizationId; _tenant = link.TenantId;
        link.IsPrimary = input.IsPrimary;
        link.UpdatedAt = DateTimeOffset.UtcNow;
        var company = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e => e.Id == link.CompanyEntityId);
        return new PersonCompanyLinkResult(link.Id.ToString(), link.CompanyEntityId.ToString(), company?.DisplayName, link.IsPrimary);
    }

    public CommandLogMetadata BuildLog(PersonCompanyLinkUpdateInput input, PersonCompanyLinkResult result, CommandContext ctx) => new()
    { ActionLabel = "Update person-company link", ResourceKind = "customers.person_company_link", ResourceId = result.Id, TenantId = _tenant, OrganizationId = _org };
}

/// <summary><c>customers.personCompanyLinks.delete</c> — soft-deletes a link.</summary>
public sealed class DeletePersonCompanyLinkCommand
    : ICommand<PersonCompanyLinkDeleteInput, PersonCompanyLinkResult>, ICommandLogMetadataBuilder<PersonCompanyLinkDeleteInput, PersonCompanyLinkResult>
{
    public string CommandId => "customers.personCompanyLinks.delete";
    private Guid? _org, _tenant;

    public async Task<PersonCompanyLinkResult> ExecuteAsync(PersonCompanyLinkDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var link = await db.Set<CustomerPersonCompanyLink>().FirstOrDefaultAsync(l => l.Id == input.LinkId && l.DeletedAt == null);
        if (link is null) throw CommandHttpException.NotFound("Person-company link not found");
        _org = link.OrganizationId; _tenant = link.TenantId;
        link.DeletedAt = DateTimeOffset.UtcNow;
        link.UpdatedAt = DateTimeOffset.UtcNow;
        return new PersonCompanyLinkResult(link.Id.ToString(), link.CompanyEntityId.ToString(), null, link.IsPrimary);
    }

    public CommandLogMetadata BuildLog(PersonCompanyLinkDeleteInput input, PersonCompanyLinkResult result, CommandContext ctx) => new()
    { ActionLabel = "Delete person-company link", ResourceKind = "customers.person_company_link", ResourceId = result.Id, TenantId = _tenant, OrganizationId = _org };
}
