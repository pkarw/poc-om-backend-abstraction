using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

// ---- Addresses -------------------------------------------------------------------------------

/// <summary><c>customers.addresses.create</c> — inserts a <c>customer_addresses</c> row. Undoable.</summary>
public sealed class CreateAddressCommand
    : ICommand<AddressCreateInput, AddressResult>, ICommandLogMetadataBuilder<AddressCreateInput, AddressResult>, IUndoableCommand
{
    public string CommandId => "customers.addresses.create";

    public async Task<AddressResult> ExecuteAsync(AddressCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var b = input.Body;
        var now = DateTimeOffset.UtcNow;
        var a = new CustomerAddress
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            EntityId = J.GuidOf(b, "entityId") ?? Guid.Empty,
            Name = J.Str(b, "name"),
            Purpose = J.Str(b, "purpose"),
            CompanyName = J.Str(b, "companyName"),
            AddressLine1 = J.Str(b, "addressLine1")?.Trim() ?? string.Empty,
            AddressLine2 = J.Str(b, "addressLine2"),
            City = J.Str(b, "city"),
            Region = J.Str(b, "region"),
            PostalCode = J.Str(b, "postalCode"),
            Country = J.Str(b, "country"),
            BuildingNumber = J.Str(b, "buildingNumber"),
            FlatNumber = J.Str(b, "flatNumber"),
            Latitude = J.Float(b, "latitude"),
            Longitude = J.Float(b, "longitude"),
            IsPrimary = J.Bool(b, "isPrimary") ?? false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<CustomerAddress>().Add(a);
        return new AddressResult(a.Id.ToString());
    }

    public CommandLogMetadata BuildLog(AddressCreateInput input, AddressResult result, CommandContext ctx) => new()
    { ActionLabel = "Create address", ResourceKind = "customers.address", ResourceId = result.AddressId, TenantId = input.TenantId, OrganizationId = input.OrganizationId };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    { var db = services.GetRequiredService<AppDbContext>(); var a = await db.Set<CustomerAddress>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!)); if (a is not null) db.Set<CustomerAddress>().Remove(a); }
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => Task.CompletedTask;
}

/// <summary><c>customers.addresses.update</c> — updates provided fields on a <c>customer_addresses</c> row.</summary>
public sealed class UpdateAddressCommand
    : ICommand<AddressUpdateInput, AddressResult>, ICommandLogMetadataBuilder<AddressUpdateInput, AddressResult>
{
    public string CommandId => "customers.addresses.update";
    private Guid? _org, _tenant;

    public async Task<AddressResult> ExecuteAsync(AddressUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var a = await db.Set<CustomerAddress>().FirstOrDefaultAsync(x => x.Id == input.Id);
        if (a is null) throw CommandHttpException.NotFound("Address not found");
        if (ctx.TenantId is { } t && a.TenantId != t) throw CommandHttpException.NotFound("Address not found");
        _org = a.OrganizationId; _tenant = a.TenantId;
        var b = input.Body;
        if (J.Has(b, "name")) a.Name = J.Str(b, "name");
        if (J.Has(b, "purpose")) a.Purpose = J.Str(b, "purpose");
        if (J.Has(b, "companyName")) a.CompanyName = J.Str(b, "companyName");
        if (J.Has(b, "addressLine1")) a.AddressLine1 = J.Str(b, "addressLine1")?.Trim() ?? a.AddressLine1;
        if (J.Has(b, "addressLine2")) a.AddressLine2 = J.Str(b, "addressLine2");
        if (J.Has(b, "city")) a.City = J.Str(b, "city");
        if (J.Has(b, "region")) a.Region = J.Str(b, "region");
        if (J.Has(b, "postalCode")) a.PostalCode = J.Str(b, "postalCode");
        if (J.Has(b, "country")) a.Country = J.Str(b, "country");
        if (J.Has(b, "buildingNumber")) a.BuildingNumber = J.Str(b, "buildingNumber");
        if (J.Has(b, "flatNumber")) a.FlatNumber = J.Str(b, "flatNumber");
        if (J.Has(b, "latitude")) a.Latitude = J.Float(b, "latitude");
        if (J.Has(b, "longitude")) a.Longitude = J.Float(b, "longitude");
        if (J.Has(b, "isPrimary")) a.IsPrimary = J.Bool(b, "isPrimary") ?? a.IsPrimary;
        a.UpdatedAt = DateTimeOffset.UtcNow;
        return new AddressResult(a.Id.ToString());
    }

    public CommandLogMetadata BuildLog(AddressUpdateInput input, AddressResult result, CommandContext ctx) => new()
    { ActionLabel = "Update address", ResourceKind = "customers.address", ResourceId = result.AddressId, TenantId = _tenant, OrganizationId = _org };
}

/// <summary><c>customers.addresses.delete</c> — hard-deletes a <c>customer_addresses</c> row.</summary>
public sealed class DeleteAddressCommand
    : ICommand<AddressDeleteInput, AddressResult>, ICommandLogMetadataBuilder<AddressDeleteInput, AddressResult>
{
    public string CommandId => "customers.addresses.delete";
    private Guid? _org, _tenant;

    public async Task<AddressResult> ExecuteAsync(AddressDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var a = await db.Set<CustomerAddress>().FirstOrDefaultAsync(x => x.Id == input.Id);
        if (a is null) throw CommandHttpException.NotFound("Address not found");
        if (ctx.TenantId is { } t && a.TenantId != t) throw CommandHttpException.NotFound("Address not found");
        _org = a.OrganizationId; _tenant = a.TenantId;
        db.Set<CustomerAddress>().Remove(a);
        return new AddressResult(a.Id.ToString());
    }

    public CommandLogMetadata BuildLog(AddressDeleteInput input, AddressResult result, CommandContext ctx) => new()
    { ActionLabel = "Delete address", ResourceKind = "customers.address", ResourceId = result.AddressId, TenantId = _tenant, OrganizationId = _org };
}

// ---- Tags ------------------------------------------------------------------------------------

/// <summary><c>customers.tags.create</c> — inserts a <c>customer_tags</c> free-pool tag. Undoable.</summary>
public sealed class CreateTagCommand
    : ICommand<TagCreateInput, TagResult>, ICommandLogMetadataBuilder<TagCreateInput, TagResult>, IUndoableCommand
{
    public string CommandId => "customers.tags.create";

    public async Task<TagResult> ExecuteAsync(TagCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var b = input.Body;
        var slug = J.Str(b, "slug")?.Trim() ?? string.Empty;
        var dup = await db.Set<CustomerTag>().AnyAsync(x => x.OrganizationId == input.OrganizationId && x.TenantId == input.TenantId && x.Slug == slug);
        if (dup) throw CommandHttpException.Conflict("A tag with this slug already exists.");
        var now = DateTimeOffset.UtcNow;
        var tag = new CustomerTag
        {
            Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, TenantId = input.TenantId,
            Slug = slug, Label = J.Str(b, "label")?.Trim() ?? slug, Color = J.Str(b, "color"), Description = J.Str(b, "description"),
            CreatedAt = now, UpdatedAt = now,
        };
        db.Set<CustomerTag>().Add(tag);
        return new TagResult(tag.Id.ToString());
    }

    public CommandLogMetadata BuildLog(TagCreateInput input, TagResult result, CommandContext ctx) => new()
    { ActionLabel = "Create tag", ResourceKind = "customers.tag", ResourceId = result.TagId, TenantId = input.TenantId, OrganizationId = input.OrganizationId };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    { var db = services.GetRequiredService<AppDbContext>(); var t = await db.Set<CustomerTag>().FirstOrDefaultAsync(x => x.Id == Guid.Parse(log.ResourceId!)); if (t is not null) db.Set<CustomerTag>().Remove(t); }
    public Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services) => Task.CompletedTask;
}

/// <summary><c>customers.tags.update</c> — updates provided fields on a <c>customer_tags</c> row.</summary>
public sealed class UpdateTagCommand
    : ICommand<TagUpdateInput, TagResult>, ICommandLogMetadataBuilder<TagUpdateInput, TagResult>
{
    public string CommandId => "customers.tags.update";
    private Guid? _org, _tenant;

    public async Task<TagResult> ExecuteAsync(TagUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var t = await db.Set<CustomerTag>().FirstOrDefaultAsync(x => x.Id == input.Id);
        if (t is null) throw CommandHttpException.NotFound("Tag not found");
        if (ctx.TenantId is { } tenant && t.TenantId != tenant) throw CommandHttpException.NotFound("Tag not found");
        _org = t.OrganizationId; _tenant = t.TenantId;
        var b = input.Body;
        if (J.Has(b, "slug")) t.Slug = J.Str(b, "slug")?.Trim() ?? t.Slug;
        if (J.Has(b, "label")) t.Label = J.Str(b, "label")?.Trim() ?? t.Label;
        if (J.Has(b, "color")) t.Color = J.Str(b, "color");
        if (J.Has(b, "description")) t.Description = J.Str(b, "description");
        t.UpdatedAt = DateTimeOffset.UtcNow;
        return new TagResult(t.Id.ToString());
    }

    public CommandLogMetadata BuildLog(TagUpdateInput input, TagResult result, CommandContext ctx) => new()
    { ActionLabel = "Update tag", ResourceKind = "customers.tag", ResourceId = result.TagId, TenantId = _tenant, OrganizationId = _org };
}

/// <summary><c>customers.tags.delete</c> — hard-deletes a <c>customer_tags</c> row (and its assignments).</summary>
public sealed class DeleteTagCommand
    : ICommand<TagDeleteInput, TagResult>, ICommandLogMetadataBuilder<TagDeleteInput, TagResult>
{
    public string CommandId => "customers.tags.delete";
    private Guid? _org, _tenant;

    public async Task<TagResult> ExecuteAsync(TagDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var t = await db.Set<CustomerTag>().FirstOrDefaultAsync(x => x.Id == input.Id);
        if (t is null) throw CommandHttpException.NotFound("Tag not found");
        if (ctx.TenantId is { } tenant && t.TenantId != tenant) throw CommandHttpException.NotFound("Tag not found");
        _org = t.OrganizationId; _tenant = t.TenantId;
        var assignments = await db.Set<CustomerTagAssignment>().Where(a => a.TagId == t.Id).ToListAsync();
        db.Set<CustomerTagAssignment>().RemoveRange(assignments);
        db.Set<CustomerTag>().Remove(t);
        return new TagResult(t.Id.ToString());
    }

    public CommandLogMetadata BuildLog(TagDeleteInput input, TagResult result, CommandContext ctx) => new()
    { ActionLabel = "Delete tag", ResourceKind = "customers.tag", ResourceId = result.TagId, TenantId = _tenant, OrganizationId = _org };
}

// ---- Tag assign / unassign -------------------------------------------------------------------

/// <summary><c>customers.tags.assign</c> — upserts a <c>customer_tag_assignments</c> row (UNIQUE tag,entity).</summary>
public sealed class AssignTagCommand
    : ICommand<TagAssignInput, TagAssignResult>, ICommandLogMetadataBuilder<TagAssignInput, TagAssignResult>
{
    public string CommandId => "customers.tags.assign";
    private Guid? _org, _tenant;

    public async Task<TagAssignResult> ExecuteAsync(TagAssignInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        _org = input.OrganizationId; _tenant = input.TenantId;
        var existing = await db.Set<CustomerTagAssignment>().FirstOrDefaultAsync(a => a.TagId == input.TagId && a.EntityId == input.EntityId);
        if (existing is not null) return new TagAssignResult(existing.Id.ToString());
        var row = new CustomerTagAssignment
        {
            Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, TenantId = input.TenantId,
            TagId = input.TagId, EntityId = input.EntityId, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Set<CustomerTagAssignment>().Add(row);
        return new TagAssignResult(row.Id.ToString());
    }

    public CommandLogMetadata BuildLog(TagAssignInput input, TagAssignResult result, CommandContext ctx) => new()
    { ActionLabel = "Assign tag", ResourceKind = "customers.tag_assignment", ResourceId = result.AssignmentId, TenantId = _tenant, OrganizationId = _org };
}

/// <summary><c>customers.tags.unassign</c> — removes a <c>customer_tag_assignments</c> row (null when none).</summary>
public sealed class UnassignTagCommand
    : ICommand<TagAssignInput, TagAssignResult>, ICommandLogMetadataBuilder<TagAssignInput, TagAssignResult>
{
    public string CommandId => "customers.tags.unassign";
    private Guid? _org, _tenant;

    public async Task<TagAssignResult> ExecuteAsync(TagAssignInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        _org = input.OrganizationId; _tenant = input.TenantId;
        var existing = await db.Set<CustomerTagAssignment>().FirstOrDefaultAsync(a => a.TagId == input.TagId && a.EntityId == input.EntityId);
        if (existing is null) return new TagAssignResult(null);
        db.Set<CustomerTagAssignment>().Remove(existing);
        return new TagAssignResult(existing.Id.ToString());
    }

    public CommandLogMetadata BuildLog(TagAssignInput input, TagAssignResult result, CommandContext ctx) => new()
    { ActionLabel = "Unassign tag", ResourceKind = "customers.tag_assignment", ResourceId = result.AssignmentId, TenantId = _tenant, OrganizationId = _org };
}
