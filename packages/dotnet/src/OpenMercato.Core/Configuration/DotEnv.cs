namespace OpenMercato.Core.Configuration;

/// <summary>
/// Minimal .env loader (mirrors upstream dotenv usage). Values already present
/// in the process environment always win, so container env vars override .env.
/// </summary>
public static class DotEnv
{
    public static void Load(string path = ".env")
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return;

        foreach (var line in File.ReadAllLines(full))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;

            var key = trimmed[..idx].Trim();
            var value = trimmed[(idx + 1)..].Trim().Trim('"');
            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
