// Stand-in for a game in launch tests. Behaviour comes from environment variables
// inherited from the test process:
//   SPECTUNER_FAKE_GAME = result (default) | crash | hang | linger (writes to stdout for 8 s, then exits)
//   SPECTUNER_FAKE_GAME_FPS = average FPS to report (default 100)
//   SPECTUNER_FAKE_GAME_LAUNCH_FILE = a file (outside the game folder) to record the arguments and the
//                                     loader's environment variables in, at start
// Without a benchmark request (started by "play") "result" mode just exits.
//   SPECTUNER_FAKE_GAME_LOG_EXTRA = extra BepInEx log lines, separated by newlines
//   SPECTUNER_FAKE_GAME_TRIAL_FPS = comma-separated FPS of the B windows of a trial, one per pair
//                                   (default: every B window = SPECTUNER_FAKE_GAME_FPS)
//   SPECTUNER_FAKE_GAME_MODS = none (default) | load | throw: what happens to the mods in
//                              BepInEx/plugins/SpecTuner (the probe aside). "load" logs what BepInEx
//                              logs for a plugin that loads and logs "loaded"; "throw" logs a plugin
//                              whose Awake throws (Unity's exception with a stack trace naming the
//                              plugin class); "none" logs nothing for them, as if never loaded.
// In "result" mode it does what the probe does at the end of a benchmark: writes
// BepInEx/LogOutput.log and BepInEx/spectuner/benchmark-<runId>.json, then exits.
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;

// The game root: the program's folder, or on macOS the folder holding <Name>.app (the program is in Contents/MacOS).
var root = AppContext.BaseDirectory;
var bundle = Path.TrimEndingDirectorySeparator(root);
if (bundle.EndsWith(Path.Combine("Contents", "MacOS"), StringComparison.Ordinal))
    root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(bundle)!)!)!;

// SPECTUNER_FAKE_GAME_LAUNCH_FILE: write the arguments and the loader's environment variables there (outside the game folder).
var launchFile = Environment.GetEnvironmentVariable("SPECTUNER_FAKE_GAME_LAUNCH_FILE");
if (!string.IsNullOrEmpty(launchFile))
{
    var environment = new JsonObject();
    foreach (var name in new[] { "DOORSTOP_ENABLED", "DOORSTOP_TARGET_ASSEMBLY", "DOORSTOP_IGNORE_DISABLED_ENV", "DOORSTOP_CLR_RUNTIME_CORECLR_PATH",
                 "LD_PRELOAD", "LD_LIBRARY_PATH", "ARCHPREFERENCE", "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH" })
        environment[name] = Environment.GetEnvironmentVariable(name);
    var launch = new JsonObject
    {
        ["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
        ["environment"] = environment,
        ["root"] = root,
    };
    File.WriteAllText(launchFile + ".tmp", launch.ToJsonString());
    File.Move(launchFile + ".tmp", launchFile, overwrite: true);
}

var mode = Environment.GetEnvironmentVariable("SPECTUNER_FAKE_GAME") ?? "result";
switch (mode)
{
    case "crash":
        return 3;
    case "hang":
        Thread.Sleep(Timeout.Infinite);
        return 0;
    case "linger": // a game the player plays for a few seconds, writing to its output as games do
        for (var i = 0; i < 16; i++)
        {
            Console.WriteLine($"fake game frame {i}");
            Thread.Sleep(500);
        }
        return 0;
}

var requestPath = Path.Combine(root, "BepInEx", "config", "spectuner.benchmark.json");
// Started by "play": no benchmark was asked for, so behave like a game the player closes at once.
if (!File.Exists(requestPath))
    return 0;
using var request = JsonDocument.Parse(File.ReadAllText(requestPath));
var runId = request.RootElement.GetProperty("runId").GetString()!;
File.Delete(requestPath);

var fps = double.Parse(Environment.GetEnvironmentVariable("SPECTUNER_FAKE_GAME_FPS") ?? "100", CultureInfo.InvariantCulture);
var frameMs = 1000.0 / fps;

var requested = new JsonObject();
var inEffect = new JsonObject();
var qualityPath = Path.Combine(root, "BepInEx", "config", "spectuner.quality.json");
if (File.Exists(qualityPath))
{
    foreach (var (key, value) in JsonNode.Parse(File.ReadAllText(qualityPath))!.AsObject())
    {
        requested[key] = value?.DeepClone();
        inEffect[key] = key != "renderScale"; // pretend the game overwrote renderScale
    }
}

Directory.CreateDirectory(Path.Combine(root, "BepInEx", "spectuner"));
File.WriteAllLines(Path.Combine(root, "BepInEx", "LogOutput.log"),
[
    "[Message:   BepInEx] BepInEx 5.4.23.6 - Fake Game",
    "[Info   :   BepInEx] Loading [SpecTuner Probe 1.0.0]",
    "[Error  : Unity Log] NullReferenceException: fake error from the fake game",
    .. ModLines(Path.Combine(root, "BepInEx", "plugins", "SpecTuner"), Environment.GetEnvironmentVariable("SPECTUNER_FAKE_GAME_MODS") ?? "none"),
    "[Message:   BepInEx] Chainloader startup complete",
    .. (Environment.GetEnvironmentVariable("SPECTUNER_FAKE_GAME_LOG_EXTRA") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries),
]);

JsonObject? trial = null;
if (request.RootElement.TryGetProperty("trial", out var trialRequest) && trialRequest.ValueKind == JsonValueKind.Object)
{
    var pairs = trialRequest.GetProperty("pairs").GetInt32();
    var bFps = (Environment.GetEnvironmentVariable("SPECTUNER_FAKE_GAME_TRIAL_FPS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(v => double.Parse(v, CultureInfo.InvariantCulture))
        .ToList();
    var windows = new JsonArray();
    var trialInEffect = new JsonObject();
    for (var i = 0; i < pairs; i++)
    {
        windows.Add(new JsonObject { ["state"] = "A", ["avgFps"] = fps, ["low1PctFps"] = fps / 2, ["frames"] = (int)fps });
        var b = i < bFps.Count ? bFps[i] : fps;
        windows.Add(new JsonObject { ["state"] = "B", ["avgFps"] = b, ["low1PctFps"] = b / 2, ["frames"] = (int)b });
    }
    var tried = JsonNode.Parse(trialRequest.GetProperty("settings").GetRawText())!.AsObject();
    foreach (var (key, _) in tried)
        trialInEffect[key] = key != "renderScale"; // pretend the game ignores renderScale
    trial = new JsonObject
    {
        ["settings"] = tried,
        ["pairs"] = pairs,
        ["windowSeconds"] = trialRequest.GetProperty("windowSeconds").GetDouble(),
        ["settleSeconds"] = trialRequest.GetProperty("settleSeconds").GetDouble(),
        ["a"] = new JsonObject { ["avgFps"] = fps },
        ["b"] = new JsonObject { ["avgFps"] = bFps.Count > 0 ? bFps.Average() : fps },
        ["windows"] = windows,
        ["inEffect"] = trialInEffect,
        ["actual"] = new JsonObject(),
    };
}

var result = new JsonObject
{
    ["runId"] = runId,
    ["scene"] = "FakeScene",
    ["frames"] = (int)(fps * 2),
    ["seconds"] = 2.0,
    ["avgFps"] = fps,
    ["low1PctFps"] = fps / 2,
    ["frameMs"] = new JsonObject { ["avg"] = frameMs, ["p50"] = frameMs, ["p95"] = frameMs * 1.5, ["p99"] = frameMs * 2, ["max"] = frameMs * 3 },
    ["resolution"] = "1280x720",
    ["settings"] = new JsonObject { ["qualityLevel"] = 2 },
    ["hardware"] = new JsonObject { ["gpu"] = "Fake GPU" },
    ["overrides"] = new JsonObject
    {
        ["requested"] = requested, ["inEffect"] = inEffect, ["actual"] = new JsonObject(),
        ["reapplied"] = new JsonObject(), ["unsupported"] = new JsonArray(), ["suspendedForMeasurement"] = new JsonArray(),
    },
    ["subWindowFps"] = trial is null ? new JsonArray(fps * 0.98, fps * 1.02, fps, fps) : null,
    ["trial"] = trial,
    ["frameCap"] = new JsonObject { ["gameVSyncCount"] = 1, ["gameTargetFrameRate"] = -1, ["reasserted"] = 0 },
};
var resultPath = Path.Combine(root, "BepInEx", "spectuner", $"benchmark-{runId}.json");
File.WriteAllText(resultPath + ".tmp", result.ToJsonString());
File.Move(resultPath + ".tmp", resultPath, overwrite: true);
return 0;

static IEnumerable<string> ModLines(string pluginDir, string mode)
{
    if (mode == "none" || !Directory.Exists(pluginDir))
        yield break;
    foreach (var dll in Directory.GetFiles(pluginDir, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
    {
        if (Path.GetFileName(dll).Equals("SpecTuner.Probe.dll", StringComparison.OrdinalIgnoreCase) || ReadPlugin(dll) is not { } plugin)
            continue;
        var (type, name, version) = plugin;
        yield return $"[Info   :   BepInEx] Loading [{name} {version}]";
        if (mode == "throw")
        {
            yield return "[Error  : Unity Log] Exception: fake failure in Awake";
            yield return "Stack trace:";
            yield return $"{type}.Awake () (at <00000000000000000000000000000000>:0)";
        }
        else
        {
            yield return $"[Info   :{name,10}] loaded";
        }
    }
}

// The class with [BepInPlugin(guid, name, version)] and the attribute's name and version, read from metadata.
static (string Type, string Name, string Version)? ReadPlugin(string dll)
{
    using var pe = new PEReader(File.OpenRead(dll));
    var md = pe.GetMetadataReader();
    foreach (var typeHandle in md.TypeDefinitions)
    {
        var type = md.GetTypeDefinition(typeHandle);
        foreach (var attributeHandle in type.GetCustomAttributes())
        {
            var attribute = md.GetCustomAttribute(attributeHandle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;
            var parent = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference || md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name) != "BepInPlugin")
                continue;
            var args = attribute.DecodeValue(new StringOnlyProvider()).FixedArguments;
            var ns = md.GetString(type.Namespace);
            var fullName = (ns.Length > 0 ? ns + "." : "") + md.GetString(type.Name);
            return (fullName, (string)args[1].Value!, (string)args[2].Value!);
        }
    }
    return null;
}

sealed class StringOnlyProvider : ICustomAttributeTypeProvider<string>
{
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSystemType() => "System.Type";
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
    public string GetTypeFromSerializedName(string name) => name;
    public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    public bool IsSystemType(string type) => type == "System.Type";
}
