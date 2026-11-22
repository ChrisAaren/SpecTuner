using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SpecTuner.Sidecar.Benchmark;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Loader;
using SpecTuner.Sidecar.Logs;
using SpecTuner.Sidecar.Platform;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>
/// A read-only report for a remote bug report: what the sidecar sees about this system and,
/// optionally, a game folder. Runs for any folder (no scope gate), writes nothing anywhere (no
/// state folder, no lock), and replaces home folders, the game and state folders, the user name
/// and the computer name before anything leaves; the finished output is checked for them again.
/// </summary>
public sealed partial class DiagnoseCommand : ICommand
{
    private const int MaxEntries = 40;
    private const int MaxPrograms = 10;
    private const int MaxLogErrors = 15;

    public string Name => "diagnose";

    public CommandResult Execute(CommandRequest request)
    {
        var gamePath = request.OptionalString("gamePath");
        var platform = Platforms.Current;
        GameFacts? facts = null;
        ScopeVerdict? verdict = null;
        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            verdict = GameInspector.Inspect(gamePath, platform);
            facts = verdict.Facts;
        }
        var redactor = new Redactor(platform.Folders.Home, facts?.FullPath, SidecarHome.Root);
        var gameExists = facts is not null && Directory.Exists(facts.FullPath);

        var data = new Dictionary<string, object?>
        {
            ["sidecar"] = new
            {
                version = typeof(DiagnoseCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0],
                runtimeIdentifier = HostFacts.RuntimeIdentifier,
                dotnet = Environment.Version.ToString(),
                platform = platform.Name,
                platformOverridden = Platforms.IsOverridden,
            },
            ["system"] = new
            {
                os = Platforms.NameOf(Platforms.Host),
                osVersion = HostFacts.OsVersion,
                distribution = HostFacts.LinuxDistribution(),
                osArchitecture = HostFacts.OsArchitecture,
                processArchitecture = HostFacts.ProcessArchitecture,
                underRosetta = HostFacts.RunsUnderRosetta(),
                processorCount = Environment.ProcessorCount,
            },
            ["stateFolder"] = new
            {
                path = redactor.Path(SidecarHome.Root),
                fromSpectunerHome = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPECTUNER_HOME")),
                exists = Directory.Exists(SidecarHome.Root),
            },
            ["environment"] = LoaderEnvironment(),
        };

        if (facts is not null)
        {
            data["game"] = Game(platform, facts, verdict!, redactor, gameExists);
            if (gameExists)
            {
                data["loader"] = Loader(platform, facts.FullPath);
                data["logs"] = LogsReport(facts, redactor);
                data["spectuner"] = StateReport(facts);
                data["gameRunning"] = facts.ExePath is not null && platform.Processes.IsRunning(facts.ExePath);
            }
        }

        var text = redactor.Text(PlainText(data));
        var summary = facts is null
            ? $"SpecTuner's view of this {Platforms.NameOf(Platforms.Host)} system, with paths and names removed."
            : verdict!.Supported
                ? $"{facts.Name} can be worked with; the report has the details, with paths and names removed."
                : $"{facts.Name ?? "This folder"} can't be worked with ({verdict.Code}); the report has the details, with paths and names removed.";
        data["text"] = text;

        // Last check: nothing identifying may leave, whatever a game file or log line contained.
        var json = redactor.Text(Json.Serialize(data));
        var cleaned = Json.Deserialize<Dictionary<string, object?>>(json)!;
        cleaned["redactions"] = redactor.Count;
        return CommandResult.Success(summary, cleaned);
    }

    private static object LoaderEnvironment()
    {
        // Names only: values are paths and may carry the user name.
        var names = Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(n => n.StartsWith("DOORSTOP_", StringComparison.Ordinal) || n is "LD_PRELOAD" or "DYLD_INSERT_LIBRARIES" or "STEAM_DYLD_INSERT_LIBRARIES")
            .Order(StringComparer.Ordinal)
            .ToList();
        return new { loaderVariablesSet = names };
    }

    private static object Game(PlatformProfile platform, GameFacts facts, ScopeVerdict verdict, Redactor redactor, bool exists)
    {
        var root = facts.FullPath;
        var normalized = root.Replace('\\', '/');
        return new
        {
            folderExists = exists,
            folder = new
            {
                steamLibrary = PlayCommand.LooksLikeSteamGame(root),
                flatpakSteam = normalized.Contains("/.var/app/com.valvesoftware.Steam/", StringComparison.Ordinal),
                caseSensitive = exists ? platform.PathsAreCaseSensitive(root) : (bool?)null,
            },
            supported = verdict.Supported,
            refusal = verdict.Supported ? null : new { code = verdict.Code, detail = redactor.Text(verdict.Detail ?? "") },
            facts,
            dataFolder = facts.DataDir is null ? null : GameTree.Relative(root, facts.DataDir),
            program = facts.ExePath is null ? null : GameTree.Relative(root, facts.ExePath),
            markers = facts.DataDir is null ? null : new
            {
                unityPlayer = File.Exists(platform.Layout.UnityPlayerPath(facts)),
                gameAssembly = File.Exists(platform.Layout.GameAssemblyPath(facts)),
                il2cppData = Directory.Exists(Path.Combine(facts.DataDir, "il2cpp_data")),
                managedDlls = facts.ManagedDir is not null && Directory.Exists(facts.ManagedDir) ? Directory.GetFiles(facts.ManagedDir, "*.dll").Length : 0,
                appInfo = File.Exists(Path.Combine(facts.DataDir, "app.info")),
            },
            entries = exists ? Entries(root) : null,
            programs = exists ? Programs(root, facts) : null,
        };
    }

    /// <summary>The folder's top level: names, kinds, sizes, permission bits (Linux, macOS).</summary>
    private static object Entries(string root)
    {
        var all = Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal).ToList();
        return new
        {
            total = all.Count,
            truncated = all.Count > MaxEntries,
            items = all.Take(MaxEntries).Select(path =>
            {
                var info = new FileInfo(path);
                var isDirectory = Directory.Exists(path);
                return new
                {
                    name = Path.GetFileName(path),
                    kind = info.LinkTarget is not null ? "link" : isDirectory ? "folder" : "file",
                    size = isDirectory ? (long?)null : info.Length,
                    mode = HostFiles.ModeString(path),
                };
            }).ToList(),
        };
    }

    /// <summary>Every file that could be a game program, with what its header says.</summary>
    private static object Programs(string root, GameFacts facts)
    {
        var candidates = new List<string>();
        candidates.AddRange(Directory.GetFiles(root, "*.exe"));
        candidates.AddRange(Directory.GetFiles(root, "*.x86_64"));
        candidates.AddRange(Directory.GetFiles(root, "*.x86"));
        candidates.AddRange(Directory.GetFiles(root).Where(p => Path.GetExtension(p).Length == 0 && ExecutableHeader.Read(p)?.Format == ExecutableHeader.Elf));
        foreach (var app in Directory.GetDirectories(root, "*.app"))
        {
            var macOs = Path.Combine(app, "Contents", "MacOS");
            if (Directory.Exists(macOs))
                candidates.AddRange(Directory.GetFiles(macOs).Where(p => ExecutableHeader.Read(p) is { Format: ExecutableHeader.MachO or ExecutableHeader.MachOUniversal }));
        }
        if (facts.ExePath is not null && !candidates.Contains(facts.ExePath))
            candidates.Insert(0, facts.ExePath);
        return candidates.Distinct().Take(MaxPrograms).Select(path =>
        {
            var header = ExecutableHeader.Read(path);
            var pe = header?.Format == ExecutableHeader.Pe ? PeFile.TryRead(path) : null;
            return new
            {
                name = GameTree.Relative(root, path),
                format = header?.Format ?? "unknown",
                arches = pe is not null ? [pe.Arch] : header?.Arches ?? [],
                peSections = pe?.SectionNames,
                slices = header?.Slices.Count > 0 ? header.Slices : null,
                executable = HostFiles.IsExecutable(path),
            };
        }).ToList();
    }

    private static object Loader(PlatformProfile platform, string root)
    {
        var kit = platform.Loader;
        var names = kit.RootFiles.Append(LoaderKit.PreloaderDll).Append(OurFiles.BepInExCoreDll).ToList();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var archive = BepInExPackage.Open(kit))
            foreach (var entry in archive.Entries.Where(e => names.Contains(e.FullName)))
            {
                using var stream = entry.Open();
                expected[entry.FullName] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
            }
        var files = names.Select(relative =>
        {
            var full = GameTree.Full(root, relative);
            var present = File.Exists(full);
            bool? matches = null;
            if (present && expected.TryGetValue(relative, out var entry))
                matches = GameTree.Sha256(full) == entry;
            return new { name = relative, present, matchesPackage = matches, executable = present ? HostFiles.IsExecutable(full) : null };
        }).ToList();

        var otherPlatformFiles = Enum.GetValues<GamePlatform>().Where(p => p != platform.Id)
            .SelectMany(p => LoaderKit.For(p).RootFiles)
            .Where(f => !kit.RootFiles.Contains(f) && File.Exists(GameTree.Full(root, f)))
            .Distinct().ToList();
        var config = GameTree.Full(root, BepInExConfig.ConfigFile);
        var entrypoint = File.Exists(config) ? EntrypointType().Match(File.ReadAllText(config)) : null;
        var core = GameTree.Full(root, OurFiles.BepInExCoreDll);
        var doorstopVersion = GameTree.Full(root, ".doorstop_version");
        return new
        {
            package = kit.ResourceName,
            files,
            bepinexVersion = File.Exists(core) ? LoaderInstaller.ReadVersion(core) : null,
            doorstopVersion = File.Exists(doorstopVersion) ? File.ReadAllText(doorstopVersion).Trim() : null,
            entrypointType = entrypoint is { Success: true } ? entrypoint.Groups[1].Value : null,
            otherPlatformLoaderFiles = otherPlatformFiles,
            melonLoader = Directory.Exists(GameTree.Full(root, "MelonLoader")),
            steamLaunchOption = kit.SteamLaunchOption("<game>"),
        };
    }

    [GeneratedRegex(@"\[Preloader\.Entrypoint\][^\[]*?^Type\s*=\s*(\S+)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex EntrypointType();

    private static object LogsReport(GameFacts facts, Redactor redactor)
    {
        object Log(string? path, bool withHeader)
        {
            if (path is null || !File.Exists(path))
                return new { path = path is null ? null : redactor.Path(path), exists = false };
            List<string> lines;
            try
            {
                lines = LogReader.ReadLines(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new { path = redactor.Path(path), exists = true, readable = false };
            }
            var errors = lines.Where(LogReader.IsError).ToList();
            return new
            {
                path = redactor.Path(path),
                exists = true,
                lastWritten = File.GetLastWriteTimeUtc(path),
                totalLines = lines.Count,
                header = withHeader ? lines.Take(12).Select(l => redactor.Text(LogReader.Clean(l))).ToList() : null,
                chainloaderStarted = withHeader ? lines.Any(l => l.Contains("Chainloader startup complete", StringComparison.Ordinal)) : (bool?)null,
                loadedPlugins = withHeader ? LogReader.LoadedPlugins(lines) : null,
                errorCount = errors.Count,
                errors = errors.TakeLast(MaxLogErrors).Select(l => redactor.Text(LogReader.Clean(l))).ToList(),
            };
        }

        return new
        {
            bepinex = Log(GameTree.Full(facts.FullPath, "BepInEx/LogOutput.log"), withHeader: true),
            player = Log(ReadLogsCommand.PlayerLogPath(facts), withHeader: false),
        };
    }

    private static object StateReport(GameFacts facts)
    {
        var state = GameState.For(facts.FullPath);
        if (!state.HasRecord)
            return new { hasRecord = false };
        var record = state.LoadRecord();
        var baseline = state.LoadBaseline();
        var ledger = baseline is null ? null : Ledger.Load(state, baseline);
        var benchmarks = BenchmarkHistory.Load(state).Runs.TakeLast(3)
            .Select(r => new { r.RunId, r.Ok, r.Failure, r.Trial, avgFps = Math.Round(r.AvgFps, 1) }).ToList();
        return new
        {
            hasRecord = true,
            loader = record?.Loader,
            probeBuilt = record?.Probe is not null,
            restores = record?.Restores.Count ?? 0,
            baselineFiles = baseline?.FileCount,
            caseSensitive = baseline?.CaseSensitive,
            changes = ledger?.Entries.GroupBy(e => $"{e.Kind}/{e.Status}{(e.Verified ? "/verified" : "")}")
                .ToDictionary(g => g.Key, g => g.Count()),
            lastBenchmarks = benchmarks,
            lockFilePresent = File.Exists(Path.Combine(state.Dir, "lock")),
            measuredGameRecorded = File.Exists(state.RunningGamePath),
        };
    }

    /// <summary>The same content as indented "key: value" lines, for pasting into a message.</summary>
    private static string PlainText(Dictionary<string, object?> data)
    {
        var builder = new StringBuilder("SpecTuner diagnosis\n");
        using var document = System.Text.Json.JsonDocument.Parse(Json.Serialize(data));
        void Write(System.Text.Json.JsonElement element, string indent)
        {
            foreach (var property in element.EnumerateObject())
            {
                var value = property.Value;
                switch (value.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Object:
                        builder.Append(indent).Append(property.Name).Append(":\n");
                        Write(value, indent + "  ");
                        break;
                    case System.Text.Json.JsonValueKind.Array:
                        builder.Append(indent).Append(property.Name).Append(":\n");
                        foreach (var item in value.EnumerateArray())
                            builder.Append(indent).Append("  - ").Append(item.ValueKind == System.Text.Json.JsonValueKind.String ? item.GetString() : item.GetRawText()).Append('\n');
                        break;
                    case System.Text.Json.JsonValueKind.Null:
                        break;
                    default:
                        builder.Append(indent).Append(property.Name).Append(": ")
                            .Append(value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : value.GetRawText()).Append('\n');
                        break;
                }
            }
        }
        Write(document.RootElement, "");
        builder.Append("Paths are shortened and user and computer names are removed.\n");
        return builder.ToString();
    }

    /// <summary>Replaces the game folder, the state folder, the home folder, the user name and the computer name.</summary>
    private sealed class Redactor
    {
        private readonly List<(string Find, string Replace)> _paths = [];
        private readonly List<Regex> _names = [];
        public int Count { get; private set; }

        public Redactor(string home, string? gameRoot, string stateRoot)
        {
            // Longest first, so the game folder inside the home folder becomes <game>, not ~/….
            foreach (var (find, replace) in new[] { (gameRoot, "<game>"), (stateRoot, "<state>"), (home, "~") }
                         .Where(p => !string.IsNullOrEmpty(p.Item1) && p.Item1!.Length > 1)
                         .OrderByDescending(p => p.Item1!.Length))
            {
                _paths.Add((find!, replace));
                if (find!.Contains('\\'))
                    _paths.Add((find.Replace('\\', '/'), replace));
                // JSON escapes backslashes: catch the escaped form when checking serialized output.
                if (find.Contains('\\'))
                    _paths.Add((find.Replace("\\", "\\\\"), replace));
            }
            foreach (var name in new[] { Environment.UserName, Environment.MachineName }.Where(n => n.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase))
                _names.Add(new Regex(@"(?<![A-Za-z0-9])" + Regex.Escape(name) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        public string Path(string path) => Text(path).Replace('\\', '/');

        public string Text(string text)
        {
            foreach (var (find, replace) in _paths)
            {
                var index = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    text = text[..index] + replace + text[(index + find.Length)..];
                    Count++;
                    index = text.IndexOf(find, index + replace.Length, StringComparison.OrdinalIgnoreCase);
                }
            }
            foreach (var name in _names)
            {
                var matches = name.Matches(text).Count;
                if (matches > 0)
                {
                    text = name.Replace(text, "<name>");
                    Count += matches;
                }
            }
            return text;
        }
    }
}
