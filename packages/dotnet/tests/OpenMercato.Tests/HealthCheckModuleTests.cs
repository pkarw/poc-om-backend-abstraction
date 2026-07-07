using OpenMercato.Modules.HealthCheck.Validators;
using Xunit;

namespace OpenMercato.Tests;

public class PingRequestValidatorTests
{
    private readonly PingRequestValidator _validator = new();

    [Fact]
    public void Accepts_valid_source()
    {
        var result = _validator.Validate(new PingRequest("integration-test"));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_missing_source(string? source)
    {
        var result = _validator.Validate(new PingRequest(source));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Rejects_source_longer_than_200_chars()
    {
        var result = _validator.Validate(new PingRequest(new string('x', 201)));

        Assert.False(result.IsValid);
    }
}
