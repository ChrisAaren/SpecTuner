namespace SpecTuner.Sidecar.Code;

/// <summary>One search result. <see cref="Snippet"/> is the matching source line for text hits; name hits have only the signature.</summary>
public sealed record CodeHit(
    string Assembly, string Type, string? Member, string Signature, int Line,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Snippet,
    string Match);

/// <summary>Searches decompiled code by name and by text, and finds types and members for read-code.</summary>
public static class CodeSearch
{
    public const int MaxSnippet = 160;

    public static (List<CodeHit> Hits, int Total) Search(IReadOnlyList<DecompiledCache.Entry> entries, string query, string kind, int limit)
    {
        var nameHits = new List<(int Rank, CodeHit Hit)>();
        var textHits = new List<CodeHit>();
        var seen = new HashSet<(string, string, int)>();

        if (kind is "any" or "name")
        {
            foreach (var entry in entries)
            {
                foreach (var type in entry.Index.Types)
                {
                    var typeName = SimpleName(type.FullName);
                    var rank = NameRank(typeName, query);
                    if (rank >= 0)
                    {
                        nameHits.Add((rank, new CodeHit(entry.Index.Assembly, type.FullName, null, type.Signature, type.StartLine, null, "name")));
                        seen.Add((entry.Index.Assembly, type.File, type.StartLine));
                    }
                    foreach (var member in type.Members)
                    {
                        rank = NameRank(member.Name, query);
                        if (rank < 0)
                            continue;
                        nameHits.Add((rank, new CodeHit(entry.Index.Assembly, type.FullName, member.Name, member.Signature, member.StartLine, null, "name")));
                        seen.Add((entry.Index.Assembly, type.File, member.StartLine));
                    }
                }
            }
        }

        if (kind is "any" or "text")
        {
            foreach (var entry in entries)
            {
                foreach (var group in entry.Index.Types.GroupBy(t => t.File))
                {
                    var lines = File.ReadAllLines(Path.Combine(entry.Dir, "types", group.Key));
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (!lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var line = i + 1;
                        if (!seen.Add((entry.Index.Assembly, group.Key, line)))
                            continue;
                        var type = group.Where(t => t.StartLine <= line && line <= t.EndLine).MinBy(t => t.EndLine - t.StartLine);
                        if (type is null)
                            continue; // usings and namespace lines
                        var member = type.Members.Where(m => m.StartLine <= line && line <= m.EndLine).MinBy(m => m.EndLine - m.StartLine);
                        textHits.Add(new CodeHit(entry.Index.Assembly, type.FullName, member?.Name, member?.Signature ?? type.Signature, line, Snip(lines[i], query), "text"));
                    }
                }
            }
        }

        var ordered = nameHits.OrderBy(h => h.Rank).Select(h => h.Hit).Concat(textHits).ToList();
        return (ordered.Take(limit).ToList(), ordered.Count);
    }

    /// <summary>0 exact, 1 prefix, 2 contains (case-insensitive); -1 no match.</summary>
    private static int NameRank(string name, string query)
    {
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        return name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : -1;
    }

    public static string SimpleName(string fullName) => fullName[(fullName.LastIndexOf('.') + 1)..];

    /// <summary>The line trimmed and, if long, cut to a window around the match.</summary>
    public static string Snip(string line, string query)
    {
        var text = line.Trim();
        if (text.Length <= MaxSnippet)
            return text;
        var at = Math.Max(0, text.IndexOf(query, StringComparison.OrdinalIgnoreCase));
        var room = MaxSnippet - 6; // leaves space for "..." on both sides
        var start = Math.Clamp(at - 40, 0, text.Length - room);
        var cut = text.Substring(start, room);
        return (start > 0 ? "..." : "") + cut + (start + cut.Length < text.Length ? "..." : "");
    }

    /// <summary>"Game.Pool`1", "Game.Pool&lt;T&gt;" and "Game.Outer+Inner" all become the index form.</summary>
    public static string NormalizeTypeName(string name)
    {
        var text = name.Trim().Replace('+', '.');
        var lt = text.IndexOf('<');
        if (lt >= 0)
            text = text[..lt];
        var tick = text.IndexOf('`');
        if (tick >= 0)
            text = text[..tick];
        return text;
    }

    public static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>Closest names: substring matches first, then by edit distance.</summary>
    public static List<string> Nearest(IEnumerable<string> names, string query, int count) =>
        names.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (Name: n, Score: n.Contains(query, StringComparison.OrdinalIgnoreCase) || query.Contains(n, StringComparison.OrdinalIgnoreCase) ? -1 : Distance(SimpleName(n), query)))
            .Where(x => x.Score < Math.Max(4, query.Length / 2))
            .OrderBy(x => x.Score).ThenBy(x => x.Name.Length)
            .Take(count)
            .Select(x => x.Name)
            .ToList();
}
