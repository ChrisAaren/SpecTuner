using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SpecTuner.Sidecar.Benchmark;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Compilation;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Loader;
using SpecTuner.Sidecar.Logs;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary><c>mods/&lt;name&gt;/plugin.json</c>: what the latest build of a mod contains, to recognise it in logs.</summary>
public sealed class ModRecord
{
    public string Name { get; set; } = "";
    public string Guid { get; set; } = "";
    public string PluginName { get; set; } = "";
    public string Version { get; set; } = "";
    public string AssemblyName { get; set; } = "";
    public List<string> Types { get; set; } = [];
    public string ChangeId { get; set; } = "";

    public static string Dir(GameState state, string name) => Path.Combine(state.Dir, "mods", name);

    public static ModRecord? Load(GameState state, string name) => AtomicFile.ReadJson<ModRecord>(Path.Combine(Dir(state, name), "plugin.json"));

    /// <summary>Mod name from its dll path, for mod changes in the ledger.</summary>
    public static string? NameFromFile(string relative) =>
        relative.StartsWith(OurFiles.PluginDir + "/", StringComparison.OrdinalIgnoreCase) && relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(relative)
            : null;
}

/// <summary>Compiles a model-written BepInEx plugin, checks it, and installs it as a "mod" change.</summary>
public sealed partial class BuildPluginCommand : ICommand
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,39}$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,60}\.cs$")]
    private static partial Regex ValidFile();

    public string Name => "build-plugin";

    public CommandResult Execute(CommandRequest request)
    {
        var name = request.RequireString("name");
        if (!ValidName().IsMatch(name) || name.Equals("SpecTuner", StringComparison.OrdinalIgnoreCase))
            throw new SidecarException(ErrorCodes.BadRequest, "\"name\" must be letters and digits, start with a letter, and be at most 40 characters.");
        var title = request.RequireString("title");
        var description = request.OptionalString("description") ?? "";
        var sources = ReadSources(request, out var fenced);
        var test = request.OptionalBool("test", fallback: true);

        var facts = GameCommands.RequireInScope(request);
        var state = GameState.For(facts.FullPath);
        var prepared = Prepare(facts, name, sources);

        using var _ = state.Lock();
        GameProcesses.EnsureNotRunning(facts);
        return Install(facts, state, prepared, title, description, sources, fenced, test, reappliedFrom: null);
    }

    /// <summary>A mod that compiled and passed every check, not yet installed.</summary>
    public sealed record PreparedMod(string Name, string AssemblyName, PluginInfo Plugin, byte[] Assembly);

    /// <summary>
    /// Compiles against the game as it is now and runs every check that can refuse (safety, compile errors,
    /// plugin shape). Touches nothing in the game folder.
    /// </summary>
    public static PreparedMod Prepare(GameFacts facts, string name, IReadOnlyList<SourceFile> sources)
    {
        var onDisk = File.Exists(GameTree.Full(facts.FullPath, OurFiles.BepInExCoreDll));
        var references = PluginCompiler.GameReferences(facts, onDisk ? null : BepInExPackage.CoreImages());
        var assemblyName = "SpecTuner.Mod." + name;
        var compilation = PluginCompiler.CreateCompilation(assemblyName, sources, references);

        var violations = SafetyCheck.Check(compilation);
        if (violations.Count > 0)
        {
            var first = violations[0];
            throw new SidecarException(ErrorCodes.UnsafeCode,
                $"The mod was refused: it uses {first.Construct} (line {first.Line}). {first.Reason} Remove it and build again.",
                $"{violations.Count} refused construct(s).", new { refused = violations });
        }

        var (errors, errorCount) = PluginCompiler.Errors(compilation);
        if (errorCount > 0)
        {
            var hint = errors.Select(e => e.Hint).FirstOrDefault(h => h is not null);
            throw new SidecarException(ErrorCodes.CompileFailed,
                $"The mod didn't compile: {errorCount} error(s). First: line {errors[0].Line}: {errors[0].Message}" + (hint is null ? "" : " Hint: " + hint),
                null, new { errors, errorCount });
        }

        var plugin = FindPlugin(compilation);
        if (plugin.Guid == "com.spectuner.probe")
            throw new SidecarException(ErrorCodes.InvalidPlugin, "The plugin GUID \"com.spectuner.probe\" belongs to SpecTuner's helper; choose another.");

        var emitted = PluginCompiler.Emit(compilation);
        if (!emitted.Success)
            throw new SidecarException(ErrorCodes.CompileFailed, "The mod didn't compile.", null, new { errors = emitted.Errors, errorCount = emitted.ErrorCount });
        return new PreparedMod(name, assemblyName, plugin, emitted.Assembly!);
    }

    /// <summary>
    /// Installs a prepared mod as one "mod" change, keeps its source, and (with <paramref name="test"/>) starts the game once to check it.
    /// The caller holds the game lock and has checked that the game is not running. reapply uses this too.
    /// </summary>
    public static CommandResult Install(GameFacts facts, GameState state, PreparedMod prepared, string title, string description,
        IReadOnlyList<SourceFile> sources, List<string> fenced, bool test, string? reappliedFrom)
    {
        var (name, assemblyName, plugin, assembly) = prepared;
        var game = GameSetup.EnsureInstalled(facts, state);

        var otherWithGuid = game.Ledger.Live
            .Where(e => e.Kind == ChangeKinds.Mod)
            .SelectMany(e => e.Files.Select(ModRecord.NameFromFile))
            .Where(n => n is not null && !n.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(n => ModRecord.Load(state, n!))
            .FirstOrDefault(r => r?.Guid == plugin.Guid);
        if (otherWithGuid is not null)
            throw new SidecarException(ErrorCodes.InvalidPlugin,
                $"The plugin GUID \"{plugin.Guid}\" is already used by the mod \"{otherWithGuid.Name}\"; BepInEx would load only one of them. Choose another GUID.");

        var relative = $"{OurFiles.PluginDir}/{name}.dll";
        var replaces = game.Ledger.Live.Any(e => e.Kind == ChangeKinds.Mod && e.Files.Contains(relative, GameTree.PathComparer));
        var change = game.Ledger.Begin(ChangeKinds.Mod, title, description);
        change.ReappliedFrom = reappliedFrom;
        try
        {
            var target = GameTree.Full(facts.FullPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, assembly);
            game.Ledger.Commit(change, [relative]);
        }
        catch
        {
            game.Ledger.Abandon(change, [relative]);
            throw;
        }

        // Keep the source of every build, for the record.
        var modDir = ModRecord.Dir(state, name);
        foreach (var source in sources)
        {
            var path = Path.Combine(modDir, change.Id, source.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source.Text);
        }
        var previousRecord = ModRecord.Load(state, name);
        var record = new ModRecord
        {
            Name = name, Guid = plugin.Guid, PluginName = plugin.Name, Version = plugin.Version,
            AssemblyName = assemblyName, Types = plugin.Types, ChangeId = change.Id,
        };
        AtomicFile.WriteJson(Path.Combine(modDir, "plugin.json"), record);

        var built = replaces
            ? $"Rebuilt the mod \"{plugin.Name}\". It replaces the previous version; undoing this change brings the previous version back."
            : $"Built and installed the mod \"{plugin.Name}\".";
        object testData;
        if (test)
        {
            var tested = TestRun(game, name, record, previousRecord, change, target: GameTree.Full(facts.FullPath, relative));
            if (tested.Failure is not null)
                return tested.Failure;
            testData = tested.Data!;
            built += replaces
                ? " SpecTuner started the game once to check it, and the new version loaded without errors."
                : " SpecTuner started the game once to check it, and the mod loaded without errors.";
        }
        else
        {
            testData = new { ran = false };
            if (!replaces)
                built += " It loads the next time the game starts.";
        }

        return CommandResult.Success(built,
            new
            {
                changeId = change.Id,
                file = relative,
                replaces,
                plugin = new { guid = plugin.Guid, name = plugin.Name, version = plugin.Version },
                lookFor = $"{plugin.Name} {plugin.Version}",
                strippedCodeFence = fenced,
                test = testData,
            });
    }

    // The shortest run that still lets the plugin load and its Update patches run for a few seconds.
    private const double TestWarmupSeconds = 2;
    private const double TestMeasureSeconds = 3;
    private const double TestStartupSeconds = 90;
    private const int MaxTestErrors = 5;
    private const int MaxTestLogLines = 10;

    private sealed record TestOutcome(object? Data, CommandResult? Failure);

    /// <summary>
    /// Starts the game once with the new mod in place and closes it. Not a benchmark: the run is not
    /// added to the benchmark history. A failed start reverts like launch-and-benchmark; a mod that did
    /// not load or logged errors is undone (bringing back an earlier build) and the rest is verified.
    /// </summary>
    private static TestOutcome TestRun(InstalledGame game, string name, ModRecord record, ModRecord? previousRecord, ChangeEntry change, string target)
    {
        var facts = game.Facts;
        var runId = "modtest-" + change.Id;
        var startedAt = DateTime.UtcNow;
        var outcome = GameLauncher.Run(facts, new BenchmarkRequest(runId, TestWarmupSeconds, TestMeasureSeconds, TestStartupSeconds));
        GameTree.DeleteFile(GameLauncher.ResultPath(facts.FullPath, runId));
        var (plugins, logErrors, lines) = GameRun.ReadThisRunsLog(facts.FullPath, startedAt);
        var playerLines = GameRun.ReadThisRunsPlayerLog(facts, startedAt);

        if (outcome.Result is null)
        {
            var (_, pluginErrors) = GameRun.ModReport(game, plugins, lines, playerLines);
            var failed = GameRun.FailedStart(game, runId, outcome, logErrors, pluginErrors);
            if (!change.IsLive)
                RestoreRecord(game.State, name, previousRecord);
            return new TestOutcome(null, failed);
        }

        var status = GameRun.Status(name, record, plugins, lines, playerLines);
        var logLines = LogReader.LinesFrom(lines, record.PluginName, MaxTestLogLines);
        var errors = status.Errors.Take(MaxTestErrors).ToList();
        if (!status.Loaded || status.Errors.Count > 0)
        {
            game.Ledger.Revert(change.Id, ChangeEndReasons.FailedTest);
            RestoreRecord(game.State, name, previousRecord);
            game.Ledger.MarkAllLiveVerified();
            var restoredPrevious = File.Exists(target);

            var message = status.Errors.Count > 0
                ? $"The mod was built, but it logged {status.Errors.Count} error(s) when the game started, so it was removed again. First error: {status.Errors[0]}"
                : "The mod was built, but the game did not load it, so it was removed again.";
            if (restoredPrevious)
                message += " The previous version of the mod is back in place.";
            return new TestOutcome(null, CommandResult.Failure(ErrorCodes.ModFailedTest, message,
                $"loaded={(status.Loaded ? "true" : "false")}; errorCount={status.Errors.Count}",
                new
                {
                    changeId = change.Id,
                    loaded = status.Loaded,
                    errorCount = status.Errors.Count,
                    errors,
                    logLines,
                    removed = true,
                    restoredPrevious,
                }));
        }

        game.Ledger.MarkAllLiveVerified();
        return new TestOutcome(new
        {
            ran = true,
            started = true,
            loaded = true,
            errorCount = 0,
            errors,
            logLines,
            seconds = Math.Round(outcome.DurationSeconds, 1),
        }, null);
    }

    /// <summary>After this build was undone, plugin.json describes the build that is back (or none).</summary>
    private static void RestoreRecord(GameState state, string name, ModRecord? previous)
    {
        var path = Path.Combine(ModRecord.Dir(state, name), "plugin.json");
        if (previous is null)
            GameTree.DeleteFile(path);
        else
            AtomicFile.WriteJson(path, previous);
    }

    /// <summary>The source kept for one build (<c>mods/&lt;name&gt;/&lt;changeId&gt;/*.cs</c>), or null when there is none.</summary>
    public static List<SourceFile>? SavedSources(GameState state, string name, string changeId)
    {
        var dir = Path.Combine(ModRecord.Dir(state, name), changeId);
        if (!Directory.Exists(dir))
            return null;
        var files = Directory.GetFiles(dir, "*.cs").Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => new SourceFile(Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
        return files.Count == 0 ? null : files;
    }

    /// <summary>The source files, each with one enclosing markdown code fence blanked (see CodeFence). <paramref name="fenced"/> names those files.</summary>
    private static List<SourceFile> ReadSources(CommandRequest request, out List<string> fenced)
    {
        fenced = [];
        var single = request.OptionalString("source");
        var hasMany = request.Root.TryGetProperty("sources", out var many) && many.ValueKind == JsonValueKind.Array;
        if ((single is not null) == hasMany)
            throw new SidecarException(ErrorCodes.BadRequest, "Give the code as either \"source\" (one text) or \"sources\" ([{ \"file\", \"code\" }]).");
        if (single is not null)
        {
            var text = CodeFence.Strip(single, out var stripped);
            if (stripped)
                fenced.Add("Plugin.cs");
            return [new SourceFile("Plugin.cs", text)];
        }

        var result = new List<SourceFile>();
        foreach (var item in many.EnumerateArray())
        {
            var file = item.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : "";
            var code = item.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : null;
            if (!ValidFile().IsMatch(file) || code is null)
                throw new SidecarException(ErrorCodes.BadRequest, "Each entry in \"sources\" needs \"file\" (a simple name ending in .cs) and \"code\".");
            if (result.Any(s => s.Path.Equals(file, StringComparison.OrdinalIgnoreCase)))
                throw new SidecarException(ErrorCodes.BadRequest, $"\"{file}\" appears twice in \"sources\".");
            var unfenced = CodeFence.Strip(code, out var wasFenced);
            if (wasFenced)
                fenced.Add(file);
            result.Add(new SourceFile(file, unfenced));
        }
        if (result.Count is 0 or > 20)
            throw new SidecarException(ErrorCodes.BadRequest, "\"sources\" needs between 1 and 20 files.");
        return result;
    }

    public sealed record PluginInfo(string Guid, string Name, string Version, List<string> Types);

    /// <summary>Exactly one class with [BepInPlugin] deriving from BaseUnityPlugin.</summary>
    private static PluginInfo FindPlugin(CSharpCompilation compilation)
    {
        var classes = compilation.SyntaxTrees
            .SelectMany(t => t.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .Select(c => compilation.GetSemanticModel(t).GetDeclaredSymbol(c)))
            .OfType<INamedTypeSymbol>()
            .ToList();

        var withAttribute = classes
            .Select(c => (Class: c, Attribute: c.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "BepInEx.BepInPlugin")))
            .Where(x => x.Attribute is not null)
            .ToList();
        const string shape = "A mod needs exactly one class marked [BepInPlugin(\"guid\", \"Name\", \"1.0.0\")] that derives from BaseUnityPlugin.";
        if (withAttribute.Count != 1)
            throw new SidecarException(ErrorCodes.InvalidPlugin, $"{shape} This code has {withAttribute.Count}.");

        var (pluginClass, attribute) = withAttribute[0];
        var derives = false;
        for (var t = pluginClass.BaseType; t is not null; t = t.BaseType)
            derives |= t.ToDisplayString() == "BepInEx.BaseUnityPlugin";
        if (!derives)
            throw new SidecarException(ErrorCodes.InvalidPlugin, $"{shape} {pluginClass.Name} does not derive from BaseUnityPlugin.");
        if (pluginClass.IsAbstract || pluginClass.IsGenericType)
            throw new SidecarException(ErrorCodes.InvalidPlugin, $"{shape} {pluginClass.Name} must not be abstract or generic.");

        var args = attribute!.ConstructorArguments.Select(a => a.Value as string).ToList();
        if (args.Count < 3 || args.Any(string.IsNullOrWhiteSpace))
            throw new SidecarException(ErrorCodes.InvalidPlugin, $"{shape} Give the GUID, name and version as plain text.");
        if (!System.Version.TryParse(args[2], out _))
            throw new SidecarException(ErrorCodes.InvalidPlugin, $"The plugin version \"{args[2]}\" must look like 1.0.0.");

        var types = classes.Select(c => c.ToDisplayString()).Distinct().ToList();
        return new PluginInfo(args[0]!, args[1]!, args[2]!, types);
    }
}
