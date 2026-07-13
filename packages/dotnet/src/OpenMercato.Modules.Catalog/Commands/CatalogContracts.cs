using System.Text.Json;

namespace OpenMercato.Modules.Catalog.Commands;

// Command input/result contracts for the catalog write surface. Create/update inputs carry the raw
// request body (JsonElement) so the handler can read base fields AND persist nested associations
// (categoryIds, tags) in the same transaction — mirroring the customers command contracts. Ids are
// strings for wire parity.

// ---- Products --------------------------------------------------------------------------------
public sealed record ProductCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record ProductUpdateInput(Guid Id, JsonElement Body);
public sealed record ProductDeleteInput(Guid Id);
public sealed record ProductResult(string? ProductId);

/// <summary>Changelog snapshot for a product's user-visible base fields (drives the auto-diff in
/// <c>CommandBus.PersistLog</c>). Bookkeeping fields (id/createdAt/updatedAt) are excluded by the diff.</summary>
public sealed record ProductSnapshot(
    string Title,
    string? Subtitle,
    string? Description,
    string? Sku,
    string? Handle,
    string ProductType,
    string? StatusEntryId,
    string? PrimaryCurrencyCode,
    string? DefaultUnit,
    string? DefaultSalesUnit,
    decimal DefaultSalesUnitQuantity,
    bool UnitPriceEnabled,
    string? UnitPriceReferenceUnit,
    string? CustomFieldsetCode,
    string? OptionSchemaId,
    bool IsConfigurable,
    bool IsActive);
