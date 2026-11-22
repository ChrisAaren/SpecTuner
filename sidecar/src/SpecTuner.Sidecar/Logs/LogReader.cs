using System.Text.RegularExpressions;

namespace SpecTuner.Sidecar.Logs;

public sealed class LogExcerpt
{
    public int TotalLines { get; init; }
    public List<string> Lines { get; init; } = [];
    public List<string> Errors { get; init; } = [];
    public int ErrorCount { get; init; }
    public bool Truncated { get; init; }
}

/// <summary>Filters game logs down to a few relevant lines, without user-profile paths.</summary>
public static partial class LogReader
{
    public const int MaxLineLength = 300;
    public const int MaxTail = 200;
    public const int MaxErrors = 20;

    [GeneratedRegex(@"\[(Error|Fatal)\s*:|Exception\b|^\s*Error[: ]|Crash!!!", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"\[Info\s*:\s*BepInEx\]\s+Loading \[(?<name>[^\]]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex PluginPattern();

    public static bool IsError(string line) => ErrorPattern().IsMatch(line);

    /// <summary>
    /// Lines containing <paramref name="filter"/> (all lines when empty), newest last, at most <paramref name="tail"/>.
    /// Errors and exceptions are returned separately and always, whatever the filter.
    /// </summary>
    public static LogExcerpt Filter(IReadOnlyList<string> lines, string? filter, int tail)
    {
        tail = Math.Clamp(tail, 1, MaxTail);
        var matching = lines.Where(l => string.IsNullOrEmpty(filter) || l.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        var errors = lines.Where(IsError).ToList();
        return new LogExcerpt
        {
            TotalLines = lines.Count,
            Lines = matching.TakeLast(tail).Select(Clean).ToList(),
            Errors = errors.TakeLast(MaxErrors).Select(Clean).ToList(),
            ErrorCount = errors.Count,
            Truncated = matching.Count > tail || errors.Count > MaxErrors,
        };
    }

    /// <summary>Plugin names and versions from a BepInEx 5 log ("Loading [Name 1.0.0]").</summary>
    public static List<string> LoadedPlugins(IEnumerable<string> lines) =>
        lines.Select(l => PluginPattern().Match(l)).Where(m => m.Success).Select(m => m.Groups["name"].Value).Distinct().ToList();

    /// <summary>Lines logged under <paramref name="sourceName"/> ("[Info   :Flight Mod] loaded"), cleaned, newest last, at most <paramref name="max"/>.</summary>
    public static List<string> LinesFrom(IReadOnlyList<string> lines, string sourceName, int max)
    {
        var source = SourceTag(sourceName);
        return lines.Where(l => source.IsMatch(l)).TakeLast(max).Select(Clean).ToList();
    }

    // BepInEx pads short source names: "[Error  :  Test Mod] ...".
    private static Regex SourceTag(string sourceName) =>
        new(@"^\[[A-Za-z]+\s*:\s*" + Regex.Escape(sourceName) + @"\]", RegexOptions.CultureInvariant);

    /// <summary>
    /// Error entries logged by <paramref name="sourceName"/> or mentioning any of
    /// <paramref name="markers"/> (assembly or type names, as found in stack traces). An entry is a line starting with '[' plus the lines after it that
    /// don't, so stack traces count. An entry counts when its first line is an error logged by
    /// <paramref name="sourceName"/>, or when it has an error and a line naming a marker. (In Unity's
    /// Player.log an exception follows whatever "[...]" line came before it, so an error after another
    /// mod's info line must not be blamed on that mod.) Each result is the entry's first error
    /// line plus the first line naming a marker when that is a different line; a first line that is not
    /// itself an error is left out.
    /// </summary>
    public static List<string> ErrorsMentioning(IReadOnlyList<string> lines, string sourceName, IReadOnlyCollection<string> markers, int max)
    {
        var source = SourceTag(sourceName);
        var result = new List<string>();
        var entry = new List<string>();

        void Flush()
        {
            var firstError = entry.FirstOrDefault(IsError);
            if (firstError is null)
                return;
            if (ReferenceEquals(firstError, entry[0]) && source.IsMatch(entry[0]))
            {
                result.Add(Clean(entry[0]));
                return;
            }
            var hit = entry.FirstOrDefault(l => markers.Any(m => l.Contains(m, StringComparison.Ordinal)));
            if (hit is null)
                return;
            // The entry's first line is part of the error only when it is an error itself. Otherwise it is
            // just the line the exception happened to follow, and leaving it out also lets one exception
            // thrown every frame count once.
            var parts = new List<string>();
            if (ReferenceEquals(firstError, entry[0]))
                parts.Add(entry[0].TrimEnd());
            else
                parts.Add(firstError.Trim());
            if (!ReferenceEquals(hit, entry[0]) && !ReferenceEquals(hit, firstError))
                parts.Add(hit.Trim());
            result.Add(Clean(string.Join(" | ", parts)));
        }

        foreach (var line in lines)
        {
            if (line.StartsWith('['))
            {
                Flush();
                entry = [];
            }
            entry.Add(line);
        }
        Flush();
        return result.Distinct().TakeLast(max).ToList();
    }

    /// <summary>Reads a log another process may still be writing.</summary>
    public static List<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    public static string Clean(string line)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0)
        {
            line = line.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                       .Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        var user = Environment.UserName;
        if (user.Length > 2)
            line = Regex.Replace(line, @"([\\/](?:Users|home)[\\/])" + Regex.Escape(user) + @"(?=[\\/])", "$1%USERNAME%", RegexOptions.IgnoreCase);
        line = line.TrimEnd();
        return line.Length > MaxLineLength ? line[..MaxLineLength] + "..." : line;
    }
}
