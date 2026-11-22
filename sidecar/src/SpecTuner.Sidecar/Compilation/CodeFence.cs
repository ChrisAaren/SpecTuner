using System.Text.RegularExpressions;

namespace SpecTuner.Sidecar.Compilation;

/// <summary>
/// Models sometimes wrap code in a markdown fence (```csharp ... ```). When the whole text,
/// ignoring surrounding whitespace, is one fenced block, the two fence lines are blanked
/// rather than removed, so every remaining line keeps its line number in the text the model
/// sent: compile errors and the saved source line up with it.
/// </summary>
public static partial class CodeFence
{
    [GeneratedRegex(@"^```[ \t]*[A-Za-z0-9_+#.\-]*[ \t]*$")]
    private static partial Regex OpeningFence();

    /// <summary>Returns the text with one enclosing fence blanked, or the text unchanged.</summary>
    public static string Strip(string text, out bool stripped)
    {
        stripped = false;
        var lines = text.Split('\n');
        var first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        var last = Array.FindLastIndex(lines, l => l.Trim().Length > 0);
        if (first < 0 || last <= first)
            return text;
        if (!OpeningFence().IsMatch(lines[first].Trim()) || lines[last].Trim() != "```")
            return text;

        lines[first] = lines[first].EndsWith('\r') ? "\r" : "";
        lines[last] = lines[last].EndsWith('\r') ? "\r" : "";
        stripped = true;
        return string.Join('\n', lines);
    }
}
