using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenMercato.Core.Commands;

namespace OpenMercato.Core.Crud;

/// <summary>A single validation issue, structurally compatible with a Zod issue (spec 02 R66).</summary>
public sealed record CrudValidationIssue(IReadOnlyList<string> Path, string Message, string Code);

/// <summary>
/// The context handed to a mutation-dispatch delegate. The delegate performs the typed
/// <see cref="CommandBus"/> call (the module owns the concrete input/result types, so generics resolve
/// there) and returns a <see cref="CrudMutationOutcome"/> the factory turns into the HTTP response.
/// </summary>
public sealed record CrudMutationContext(
    HttpContext Http,
    CommandContext Ctx,
    JsonElement Body,
    IReadOnlyDictionary<string, string> Query,
    CommandBus Bus,
    IServiceProvider Services);

/// <summary>The result of a mutation dispatch: the affected record id, the action-log row, and an
/// optional custom response body (defaults to the standard <c>{id}</c>/<c>{ok:true}</c> envelopes).</summary>
public sealed record CrudMutationOutcome(string? Id, ActionLog? Log, object? Result = null);

/// <summary>Performs the typed command-bus call for one CRUD mutation (create/update/delete).</summary>
public delegate Task<CrudMutationOutcome> CrudDispatch(CrudMutationContext m);

/// <summary>
/// Declarative configuration for a CRUD entity — the port of upstream <c>CrudFactoryOptions</c>
/// (packages/shared/src/lib/crud/factory.ts). A module builds one of these and calls
/// <see cref="CrudRoute.Map{TEntity}"/> from its <c>MapRoutes</c> to register the 5 standard endpoints.
///
/// The factory owns the observable pipeline (auth, query-param parsing, envelope, status codes,
/// soft-delete + tenant/org scoping, events, custom-field/indexer hooks, error mapping). Entity-specific
/// concerns are supplied through the delegates below so the factory stays generic.
/// </summary>
public sealed class CrudConfig<TEntity> where TEntity : class
{
    // ---- Route identity -----------------------------------------------------------------------

    /// <summary>URL base under <c>/api/</c>, e.g. <c>"customers/people"</c> → <c>/api/customers/people</c>.</summary>
    public required string BasePath { get; init; }

    /// <summary>Entity id <c>'&lt;module&gt;:&lt;entity&gt;'</c> for the indexer + custom-fields (spec 03 R7).</summary>
    public required string EntityType { get; init; }

    /// <summary>Resource kind for action logs / optimistic lock, e.g. <c>"customers.person"</c>.</summary>
    public required string ResourceKind { get; init; }

    // ---- List behaviour -----------------------------------------------------------------------

    public int DefaultPageSize { get; init; } = 50;
    public int MaxPageSize { get; init; } = 100;
    public string DefaultSortField { get; init; } = "id";

    /// <summary>When true (default) reads exclude rows with a non-null <c>deleted_at</c> unless <c>?withDeleted=true</c>.</summary>
    public bool SoftDelete { get; init; } = true;

    /// <summary>
    /// When true the entity is organization-scoped: an empty org scope yields a 200 empty list on GET
    /// and a 403 <c>{error:"Forbidden"}</c> on mutations (spec 02 R27/R32), and the org filter is applied.
    /// </summary>
    public bool OrgScoped { get; init; } = true;

    // ---- Per-method RBAC features (spec 02 R10) -----------------------------------------------

    public string[]? ListFeatures { get; init; }
    /// <summary>Defaults to <see cref="ListFeatures"/> when unset.</summary>
    public string[]? GetFeatures { get; init; }
    public string[]? CreateFeatures { get; init; }
    public string[]? UpdateFeatures { get; init; }
    public string[]? DeleteFeatures { get; init; }

    // ---- Query building (generic EF; factory owns scope + soft-delete + pagination) -----------

    /// <summary>Selects the base uuid PK (used for <c>?ids=</c>, single-item fetch, and default sort).</summary>
    public required Expression<Func<TEntity, Guid>> IdSelector { get; init; }

    /// <summary>Selects <c>deleted_at</c>; when set (and <see cref="SoftDelete"/>), the null filter is applied.</summary>
    public Expression<Func<TEntity, DateTimeOffset?>>? DeletedAtSelector { get; init; }

    /// <summary>Selects <c>tenant_id</c>; when set, reads are filtered by <see cref="CommandContext.TenantId"/>.</summary>
    public Expression<Func<TEntity, Guid?>>? TenantIdSelector { get; init; }

    /// <summary>Selects <c>organization_id</c>; when set (and <see cref="OrgScoped"/>), org-scope filtering applies.</summary>
    public Expression<Func<TEntity, Guid?>>? OrganizationIdSelector { get; init; }

    /// <summary>Named sort delegates (field name → ordered query). Missing fields fall back to id sort.</summary>
    public IReadOnlyDictionary<string, Func<IQueryable<TEntity>, bool, IOrderedQueryable<TEntity>>>? Sorts { get; init; }

    /// <summary>Applies entity-specific filters + free-text search onto the query (spec 02 R21/R23/R45).</summary>
    public Func<IQueryable<TEntity>, CrudListQuery, CommandContext, IQueryable<TEntity>>? ApplyFilters { get; init; }

    /// <summary>Projects an entity row into a mutable list-item map (so custom fields can be merged in).</summary>
    public required Func<TEntity, IDictionary<string, object?>> ProjectItem { get; init; }

    /// <summary>Projects an entity for the detail endpoint; defaults to <see cref="ProjectItem"/>.</summary>
    public Func<TEntity, IDictionary<string, object?>>? ProjectDetail { get; init; }

    /// <summary>Post-list decorator (upstream <c>afterList</c> hook); runs after custom-field decoration.</summary>
    public Func<IReadOnlyList<IDictionary<string, object?>>, CommandContext, HttpContext, Task>? ListHook { get; init; }

    // ---- Lifecycle events (spec 03 R50) -------------------------------------------------------

    public string? CreatedEvent { get; init; }
    public string? UpdatedEvent { get; init; }
    public string? DeletedEvent { get; init; }

    // ---- Validation (FluentValidation-friendly; spec 02 R34/R66) ------------------------------

    public Func<JsonElement, IReadOnlyList<CrudValidationIssue>>? ValidateCreate { get; init; }
    public Func<JsonElement, IReadOnlyList<CrudValidationIssue>>? ValidateUpdate { get; init; }

    // ---- Mutation dispatch (typed command-bus calls owned by the module) ----------------------

    public CrudDispatch? CreateDispatch { get; init; }
    public CrudDispatch? UpdateDispatch { get; init; }
    public CrudDispatch? DeleteDispatch { get; init; }

    // ---- Response overrides (defaults per spec 02 R31/R54) ------------------------------------

    /// <summary>Build the POST body; defaults to <c>{ id }</c>.</summary>
    public Func<CrudMutationOutcome, object>? CreateResponse { get; init; }
    public int CreateStatus { get; init; } = 201;

    /// <summary>Build the PUT body; defaults to <c>{ ok = true }</c>.</summary>
    public Func<CrudMutationOutcome, object>? UpdateResponse { get; init; }

    /// <summary>Build the DELETE body; defaults to <c>{ ok = true }</c>.</summary>
    public Func<CrudMutationOutcome, object>? DeleteResponse { get; init; }
}
