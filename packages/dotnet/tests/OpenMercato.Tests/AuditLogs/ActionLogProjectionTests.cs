using OpenMercato.Core.Commands;
using Xunit;

namespace OpenMercato.Tests.AuditLogs;

public class ActionLogProjectionTests
{
    [Fact]
    public void DiffSnapshots_emits_changed_fields_with_from_to()
    {
        var before = "{\"displayName\":\"Ada\",\"status\":\"active\",\"score\":1}";
        var after = "{\"displayName\":\"Ada Lovelace\",\"status\":\"active\",\"score\":2}";
        var diff = ActionLogProjection.DiffSnapshots(before, after);
        Assert.True(diff.ContainsKey("displayName"));
        Assert.True(diff.ContainsKey("score"));
        Assert.False(diff.ContainsKey("status")); // unchanged
    }

    [Theory]
    [InlineData("customers.people.update", "update")]
    [InlineData("customers.companies.delete", "delete")]
    [InlineData("customers.people.edit", "update")]
    [InlineData("customers.interactions.create", "create")]
    [InlineData("some.random.command", null)]
    public void DeriveActionType_maps_command_verb(string commandId, string? expected)
    {
        Assert.Equal(expected, ActionLogProjection.DeriveActionType(commandId));
    }
}
