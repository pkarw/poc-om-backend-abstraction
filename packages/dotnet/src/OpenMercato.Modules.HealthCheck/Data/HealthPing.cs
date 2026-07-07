namespace OpenMercato.Modules.HealthCheck.Data;

/// <summary>
/// Entity mapped to table om_health_ping (upstream equivalent: data/entities.ts).
/// The table/column mapping lives in HealthCheckModule.ConfigureModel.
/// </summary>
public sealed class HealthPing
{
    public Guid Id { get; set; }

    public string Source { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
