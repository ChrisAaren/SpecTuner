using System.Text;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Code;

namespace SpecTuner.Sidecar.Commands;

/// <summary>Returns one member's decompiled source, a whole type, or a type outline, with line numbers.</summary>
public sealed class ReadCodeCommand : ICommand
{
    public const int MaxLines = 400;

    public string Name => "read-code";

    public CommandResult Execute(CommandRequest request)
    {
        var typeQuery = request.RequireString("type");
        var memberQuery = request.OptionalString("member");
        var maxLines = (int)request.OptionalNumber("maxLines", 200, 10, MaxLines);
        var startLine = request.Root.TryGetProperty("startLine", out _) ? (int?)request.OptionalNumber("startLine", 1, 1, int.MaxValue) : null;

        var facts = GameCommands.RequireInScope(request);
        var entries = CodeCommands.Load(facts, request.OptionalString("assembly"));
        var (entry, type) = FindType(entries, typeQuery);
        var lines = File.ReadAllLines(entry.TypeFile(type));

        if (memberQuery is not null)
            return ReadMember(entry, type, lines, memberQuery, maxLines);

        var typeLines = type.EndLine - type.StartLine + 1;
        if (startLine is null && typeLines > maxLines)
            return Outline(entry, type, maxLines);

        var from = Math.Clamp(startLine ?? type.StartLine, type.StartLine, type.EndLine);
        var to = Math.Min(type.EndLine, from + maxLines - 1);
        return CommandResult.Success($"{type.FullName}, lines {from}-{to} of {type.StartLine}-{type.EndLine}.", new
        {
            assembly = entry.Index.Assembly,
            type = type.FullName,
            type.Kind,
            type.Signature,
            from,
            to,
            typeLines = new { from = type.StartLine, to = type.EndLine },
            code = Numbered(lines, from, to),
            truncated = to < type.EndLine,
            note = to < type.EndLine ? $"More follows: ask again with \"startLine\": {to + 1}." : null,
        });
    }

    private static CommandResult ReadMember(DecompiledCache.Entry entry, TypeEntry type, string[] lines, string memberQuery, int maxLines)
    {
        var wanted = memberQuery is ".ctor" or "ctor" or "constructor" ? CodeSearch.SimpleName(type.FullName) : memberQuery;
        var matches = type.Members.Where(m => m.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(m => (m.StartLine, m.EndLine))
            .ToList();
        if (matches.Count == 0)
            throw new SidecarException(ErrorCodes.MemberNotFound, $"{type.FullName} has no member called \"{memberQuery}\".",
                null, new { nearest = CodeSearch.Nearest(type.Members.Select(m => m.Name), memberQuery, 8) });

        var code = new StringBuilder();
        var used = 0;
        var shown = 0;
        foreach (var member in matches)
        {
            var count = member.EndLine - member.StartLine + 1;
            if (used > 0 && used + count > maxLines)
                break;
            var to = Math.Min(member.EndLine, member.StartLine + (maxLines - used) - 1);
            if (code.Length > 0)
                code.Append('\n');
            code.Append(Numbered(lines, member.StartLine, to));
            used += to - member.StartLine + 1;
            shown++;
        }

        return CommandResult.Success(
            $"{type.FullName}.{matches[0].Name}: {matches.Count} declaration(s){(shown < matches.Count ? $", {shown} shown" : "")}.",
            new
            {
                assembly = entry.Index.Assembly,
                type = type.FullName,
                members = matches.Select(m => new { m.Name, m.Kind, m.Signature, from = m.StartLine, to = m.EndLine }),
                code = code.ToString(),
                truncated = shown < matches.Count || used >= maxLines && matches.Sum(m => m.EndLine - m.StartLine + 1) > used,
            });
    }

    private static CommandResult Outline(DecompiledCache.Entry entry, TypeEntry type, int maxLines)
    {
        var fields = type.Members.Where(m => m.Kind is "field" or "event" or "enum value").Select(m => $"{m.StartLine}: {m.Signature}").ToList();
        var members = type.Members.Where(m => m.Kind is not ("field" or "event" or "enum value")).Select(m => $"{m.StartLine}: {m.Signature}").ToList();
        var prefix = type.FullName + ".";
        var nested = entry.Index.Types
            .Where(t => t.File == type.File && t.FullName.StartsWith(prefix, StringComparison.Ordinal) && !t.FullName[prefix.Length..].Contains('.'))
            .Select(t => $"{t.StartLine}: {t.Signature}")
            .ToList();
        return CommandResult.Success(
            $"{type.FullName} has {type.EndLine - type.StartLine + 1} lines, more than {maxLines}, so here is its outline.",
            new
            {
                assembly = entry.Index.Assembly,
                type = type.FullName,
                type.Kind,
                type.Signature,
                typeLines = new { from = type.StartLine, to = type.EndLine },
                fields,
                members,
                nestedTypes = nested,
                note = "Ask for one member with \"member\", or for a line range with \"startLine\".",
            });
    }

    /// <summary>Finds a type by full or simple name. Ambiguous simple names and unknown names are refused with candidates.</summary>
    public static (DecompiledCache.Entry Entry, TypeEntry Type) FindType(IReadOnlyList<DecompiledCache.Entry> entries, string query)
    {
        var name = CodeSearch.NormalizeTypeName(query);
        var all = entries.SelectMany(e => e.Index.Types.Select(t => (Entry: e, Type: t))).ToList();

        var exact = all.Where(x => x.Type.FullName.Equals(name, StringComparison.Ordinal)).ToList();
        if (exact.Count == 0)
            exact = all.Where(x => x.Type.FullName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 0)
            exact = all.Where(x => x.Type.FullName.EndsWith("." + name, StringComparison.OrdinalIgnoreCase)).ToList();

        if (exact.Count == 1)
            return exact[0];
        if (exact.Count > 1)
            throw new SidecarException(ErrorCodes.AmbiguousType, $"More than one type is called \"{query}\". Ask again with the full name.",
                null, new { candidates = exact.Take(20).Select(x => new { type = x.Type.FullName, x.Type.Kind, assembly = x.Entry.Index.Assembly }) });

        throw new SidecarException(ErrorCodes.TypeNotFound, $"The game's code has no type called \"{query}\".",
            null, new { nearest = CodeSearch.Nearest(all.Select(x => x.Type.FullName), CodeSearch.SimpleName(name), 8) });
    }

    private static string Numbered(string[] lines, int from, int to)
    {
        var sb = new StringBuilder();
        for (var line = from; line <= to && line <= lines.Length; line++)
        {
            if (sb.Length > 0)
                sb.Append('\n');
            sb.Append(line).Append(": ").Append(lines[line - 1].TrimEnd());
        }
        return sb.ToString();
    }
}
