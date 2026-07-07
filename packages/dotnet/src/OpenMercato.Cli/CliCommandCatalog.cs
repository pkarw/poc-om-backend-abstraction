using OpenMercato.Cli.Commands;
using OpenMercato.Core.Modules;

namespace OpenMercato.Cli;

/// <summary>The host-provided built-in commands (parallels the inline built-ins in upstream cli.ts).</summary>
public static class CliCommandCatalog
{
    public static IReadOnlyList<ICliCommand> BuiltIns() => new ICliCommand[]
    {
        new MigrateCommand(),
        new InitCommand(),
        new GreenfieldCommand(),
        new SeedCommand(),
    };
}
