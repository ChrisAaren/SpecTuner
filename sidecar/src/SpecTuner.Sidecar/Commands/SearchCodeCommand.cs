using System.Diagnostics;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Code;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>Finds types, members and source lines in the game's decompiled code. Changes nothing in the game folder.</summary>
public sealed class SearchCodeCommand : ICommand
{
    public const int MaxLimit = 50;

    public string Name => "search-code";

    public CommandResult Execute(CommandRequest request)
    {
        var query = request.RequireString("query").Trim();
        if (query.Length < 2)
            throw new SidecarException(ErrorCodes.BadRequest, "\"query\" needs at least 2 characters.");
        var kind = request.OptionalString("kind") ?? "any";
        if (kind is not ("any" or "name" or "text"))
            throw new SidecarException(ErrorCodes.BadRequest, "\"kind\" must be \"any\", \"name\" or \"text\".");
        var limit = (int)request.OptionalNumber("limit", 20, 1, MaxLimit);

        var facts = GameCommands.RequireInScope(request);
        var clock = Stopwatch.StartNew();
        var entries = CodeCommands.Load(facts, request.OptionalString("assembly"));
        var (hits, total) = CodeSearch.Search(entries, query, kind, limit);

        return CommandResult.Success(
            total == 0 ? $"Nothing in the game's code matches \"{query}\"." : $"{total} place(s) in the game's code match \"{query}\".",
            new
            {
                hits,
                total,
                truncated = total > hits.Count,
                assemblies = entries.Select(e => new
                {
                    name = e.Index.Assembly, types = e.Index.Types.Count, fromCache = e.FromCache, decompileSeconds = e.FromCache ? (double?)null : e.Index.DecompileSeconds,
                }),
                seconds = Math.Round(clock.Elapsed.TotalSeconds, 2),
            });
    }
}

public static class CodeCommands
{
    /// <summary>The decompiled game assemblies (or the one named), decompiling on first use.</summary>
    public static List<DecompiledCache.Entry> Load(GameFacts facts, string? assembly)
    {
        var state = GameState.For(facts.FullPath);
        List<string> paths;
        if (assembly is not null)
        {
            var path = GameAssemblies.Find(facts, assembly)
                ?? throw new SidecarException(ErrorCodes.AssemblyNotFound, $"The game has no code library called \"{assembly}\".",
                    "The game's own libraries: " + string.Join(", ", GameAssemblies.Own(facts).Select(Path.GetFileNameWithoutExtension)));
            paths = [path];
        }
        else
        {
            paths = GameAssemblies.Own(facts);
            if (paths.Count == 0)
                throw new SidecarException(ErrorCodes.AssemblyNotFound, "SpecTuner couldn't find any of the game's own code to search.");
        }
        return paths.Select(p => DecompiledCache.Load(state, facts, p)).ToList();
    }
}
