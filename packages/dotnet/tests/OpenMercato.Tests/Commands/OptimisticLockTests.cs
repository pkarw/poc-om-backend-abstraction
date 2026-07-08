using OpenMercato.Core.Commands;
using Xunit;

namespace OpenMercato.Tests.Commands;

/// <summary>
/// Tests for the command-level optimistic-lock helper (port of enforceCommandOptimisticLock):
/// mismatch → 409 record_modified body; match / missing token / disabled env → no-op.
/// </summary>
public class OptimisticLockTests
{
    private static CommandContext CtxWithHeader(string? expected)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (expected is not null) headers[OptimisticLock.HeaderName] = expected;
        return new CommandContext { Headers = headers };
    }

    [Fact]
    public void Mismatch_throws_409_with_record_modified_body()
    {
        var current = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
        var ctx = CtxWithHeader("2026-07-07T09:00:00.000Z");

        var ex = Assert.Throws<CommandHttpException>(() =>
            OptimisticLock.Enforce("test.widget", "id-1", current, ctx, envValue: "all"));

        Assert.Equal(409, ex.Status);
        var body = Assert.IsType<OptimisticLock.ConflictBody>(ex.Body);
        Assert.Equal("record_modified", body.error);
        Assert.Equal("optimistic_lock_conflict", body.code);
        Assert.Equal("2026-07-07T09:00:00.000Z", body.expectedUpdatedAt);
        Assert.Equal("2026-07-07T10:00:00.000Z", body.currentUpdatedAt);
    }

    [Fact]
    public void Matching_version_is_a_noop()
    {
        var current = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
        var ctx = CtxWithHeader("2026-07-07T10:00:00.000Z");

        OptimisticLock.Enforce("test.widget", "id-1", current, ctx, envValue: "all");
    }

    [Fact]
    public void Missing_expected_token_is_a_noop_even_on_change()
    {
        var current = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
        var ctx = CtxWithHeader(null); // client did not send the header

        OptimisticLock.Enforce("test.widget", "id-1", current, ctx, envValue: "all");
    }

    [Fact]
    public void Disabled_env_skips_the_guard_even_on_mismatch()
    {
        var current = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
        var ctx = CtxWithHeader("2026-07-07T09:00:00.000Z");

        OptimisticLock.Enforce("test.widget", "id-1", current, ctx, envValue: "off");
    }

    [Fact]
    public void Allowlist_env_scopes_by_resource_kind()
    {
        var current = new DateTime(2026, 7, 7, 10, 0, 0, DateTimeKind.Utc);
        var ctx = CtxWithHeader("2026-07-07T09:00:00.000Z");

        // Not in the allow-list → skipped.
        OptimisticLock.Enforce("test.widget", "id-1", current, ctx, envValue: "sales.order");

        // In the allow-list → enforced.
        Assert.Throws<CommandHttpException>(() =>
            OptimisticLock.Enforce("sales.order", "id-1", current, ctx, envValue: "sales.order"));
    }

    [Theory]
    [InlineData(null, OptimisticLock.Mode.All)]
    [InlineData("", OptimisticLock.Mode.All)]
    [InlineData("all", OptimisticLock.Mode.All)]
    [InlineData("off", OptimisticLock.Mode.Off)]
    [InlineData("false", OptimisticLock.Mode.Off)]
    [InlineData("customers.company,sales.order", OptimisticLock.Mode.Allowlist)]
    public void ParseEnv_grammar(string? raw, OptimisticLock.Mode expected)
    {
        Assert.Equal(expected, OptimisticLock.ParseEnv(raw).Mode);
    }
}
