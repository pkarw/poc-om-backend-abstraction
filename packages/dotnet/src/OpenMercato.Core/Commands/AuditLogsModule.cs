using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Modules;

namespace OpenMercato.Core.Commands;

/// <summary>
/// The audit_logs module (upstream packages/core/src/modules/audit_logs) — port scope limited to the
/// command-write infrastructure: it maps the <c>action_logs</c> table (<see cref="ActionLog"/>) onto
/// the shared DbContext and registers the command-write services (<see cref="ActionLogService"/>,
/// <see cref="CommandBus"/>). Its byte-exact DDL is created by the raw-SQL migration
/// <c>20260707040000_AddActionLogs</c>.
///
/// The module lives in OpenMercato.Core (next to the command bus) so every host that references Core
/// gets the command infrastructure without a new project. Registering commands is a per-module
/// concern: each module adds its handlers via <c>services.AddScoped&lt;ICommand, XCommand&gt;()</c>.
///
/// PARITY-TODO: the audit_logs HTTP API (list / undo / redo / access routes), the AccessLog table,
/// projections/encryption, and CRUD-cache/side-effect flushing are deferred to the audit_logs API
/// port + CRUD factory. <see cref="MapRoutes"/> is therefore a no-op here.
/// </summary>
public sealed class AuditLogsModule : IModule
{
    public string Id => "audit_logs";

    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "audit_logs.view",
        "audit_logs.undo",
    };

    public void ConfigureServices(IServiceCollection services)
    {
        // di.ts equivalents: the action-log persistence service + the command bus. Scoped because both
        // depend on the request-scoped AppDbContext (and the bus resolves scoped ICommand handlers).
        services.AddScoped<ActionLogService>();
        services.AddScoped<CommandBus>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActionLog>(e =>
        {
            e.ToTable("action_logs");
            e.HasKey(x => x.Id).HasName("action_logs_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.CommandId).HasColumnName("command_id");
            e.Property(x => x.ActionLabel).HasColumnName("action_label");
            e.Property(x => x.ActionType).HasColumnName("action_type");
            e.Property(x => x.ResourceKind).HasColumnName("resource_kind");
            e.Property(x => x.ResourceId).HasColumnName("resource_id");
            e.Property(x => x.ParentResourceKind).HasColumnName("parent_resource_kind");
            e.Property(x => x.ParentResourceId).HasColumnName("parent_resource_id");
            e.Property(x => x.ExecutionState).HasColumnName("execution_state");
            e.Property(x => x.UndoToken).HasColumnName("undo_token");
            e.Property(x => x.CommandPayload).HasColumnName("command_payload").HasColumnType("jsonb");
            e.Property(x => x.SnapshotBefore).HasColumnName("snapshot_before").HasColumnType("jsonb");
            e.Property(x => x.SnapshotAfter).HasColumnName("snapshot_after").HasColumnType("jsonb");
            e.Property(x => x.ChangesJson).HasColumnName("changes_json").HasColumnType("jsonb");
            e.Property(x => x.ChangedFields).HasColumnName("changed_fields").HasColumnType("text[]");
            e.Property(x => x.PrimaryChangedField).HasColumnName("primary_changed_field");
            e.Property(x => x.ContextJson).HasColumnName("context_json").HasColumnType("jsonb");
            e.Property(x => x.SourceKey).HasColumnName("source_key");
            e.Property(x => x.RelatedResourceKind).HasColumnName("related_resource_kind");
            e.Property(x => x.RelatedResourceId).HasColumnName("related_resource_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });
    }

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        // PARITY-TODO: audit_logs API routes (list / actions/undo / actions/redo / access) land with
        // the audit_logs API port + CRUD factory.
    }
}
