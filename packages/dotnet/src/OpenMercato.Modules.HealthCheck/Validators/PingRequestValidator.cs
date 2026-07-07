using FluentValidation;

namespace OpenMercato.Modules.HealthCheck.Validators;

/// <summary>Request body for POST /api/health_check/ping.</summary>
public sealed record PingRequest(string? Source);

/// <summary>
/// FluentValidation validator (upstream equivalent: data/validators.ts with Zod).
/// </summary>
public sealed class PingRequestValidator : AbstractValidator<PingRequest>
{
    public PingRequestValidator()
    {
        RuleFor(x => x.Source)
            .NotEmpty()
            .MaximumLength(200);
    }
}
