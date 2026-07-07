using System.Text.RegularExpressions;

namespace OpenMercato.Modules.Directory.Lib;

/// <summary>1:1 port of shared/src/lib/slugify.ts (default replacement '-', allowed chars '-').</summary>
public static class Slugify
{
    public static string Run(string value)
    {
        const string replacement = "-";
        var normalized = value.ToLowerInvariant().Trim();
        if (normalized.Length == 0) return "";
        // allowedChars '-' escaped in the character class; invalid = [^a-z0-9\-]+ → replacement.
        var replaced = Regex.Replace(normalized, "[^a-z0-9\\-]+", replacement);
        if (replaced.Length == 0) return replaced;
        // trimReplacement: strip leading/trailing replacement char.
        var start = 0;
        var end = replaced.Length;
        while (start < end && replaced[start] == '-') start++;
        while (end > start && replaced[end - 1] == '-') end--;
        return replaced.Substring(start, end - start);
    }
}
