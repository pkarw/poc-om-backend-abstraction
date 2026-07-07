namespace OpenMercato.Core.Modules;

/// <summary>
/// A CLI subcommand contributed by a module (parallels one entry in an upstream module's
/// <c>cli.ts</c> default export). The global CLI host (OpenMercato.Cli) discovers these via
/// <see cref="IModule.CliCommands"/>, aggregates them with the built-in commands and dispatches by
/// <see cref="Name"/>. Implementations resolve what they need (AppDbContext, crypto services, the
/// registry) from the provided <see cref="IServiceProvider"/>, typically inside a new DI scope.
/// </summary>
public interface ICliCommand
{
    /// <summary>Dispatch name, e.g. "add-user" (matched case-insensitively).</summary>
    string Name { get; }

    /// <summary>One-line description shown in the CLI help listing.</summary>
    string Description { get; }

    /// <summary>Run the command with the remaining args (after the command name). Returns a process exit code.</summary>
    Task<int> RunAsync(string[] args, IServiceProvider services);
}

/// <summary>
/// Minimal <c>--key value</c> / <c>--key=value</c> argument parser shared by CLI command
/// implementations (parallels the ad-hoc parsers in upstream module <c>cli.ts</c> files). A bare
/// <c>--flag</c> (no following value) is recorded as "true".
/// </summary>
public static class CliArgs
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (token is null || !token.StartsWith("--", StringComparison.Ordinal)) continue;
            var key = token[2..];
            string? value = null;
            var eq = key.IndexOf('=');
            if (eq >= 0)
            {
                value = key[(eq + 1)..];
                key = key[..eq];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }
            if (key.Length == 0) continue;
            result[key] = value ?? "true";
        }
        return result;
    }

    /// <summary>First non-null/non-blank value among the given keys, else null.</summary>
    public static string? Get(this Dictionary<string, string> args, params string[] keys)
    {
        foreach (var key in keys)
            if (args.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;
        return null;
    }
}
