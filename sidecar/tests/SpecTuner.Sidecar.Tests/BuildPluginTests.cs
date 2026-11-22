using System.Text.Json;
using Microsoft.CodeAnalysis;
using SpecTuner.Sidecar.Compilation;
using SpecTuner.Sidecar.Loader;
using SpecTuner.Sidecar.State;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

public static class ModSources
{
    public static string Plugin(string body = "", string usings = "", string guid = "test.mod", string name = "Test Mod", string version = "1.0.0", string extraTypes = "", string ns = "TestMod") => $$"""
        using BepInEx;
        using HarmonyLib;
        using UnityEngine;
        {{usings}}

        namespace {{ns}}
        {
            [BepInPlugin("{{guid}}", "{{name}}", "{{version}}")]
            public class Plugin : BaseUnityPlugin
            {
                private void Awake()
                {
                    new Harmony("{{guid}}").PatchAll();
                    Logger.LogInfo("loaded");
                    {{body}}
                }
            }
            {{extraTypes}}
        }
        """;
}

[TestClass]
public sealed class BuildPluginTests : SidecarTest
{
    private static JsonElement Build(GameFixture game, string source, string name = "TestMod", string title = "Test mod", bool test = false) =>
        Run("build-plugin", new { gamePath = game.Root, name, title, description = "For a test.", source, test });

    [TestMethod]
    public void Success_InstallsTheDll_RecordsAModChange_AndKeepsTheSource()
    {
        var game = CreateGame();

        var envelope = Build(game, ModSources.Plugin());

        AssertOk(envelope);
        var data = envelope.GetProperty("data");
        Assert.AreEqual("test.mod", data.GetProperty("plugin").GetProperty("guid").GetString());
        Assert.AreEqual("Test Mod", data.GetProperty("plugin").GetProperty("name").GetString());
        Assert.AreEqual("1.0.0", data.GetProperty("plugin").GetProperty("version").GetString());
        Assert.AreEqual("Test Mod 1.0.0", data.GetProperty("lookFor").GetString());
        Assert.IsTrue(game.Exists("BepInEx/plugins/SpecTuner/TestMod.dll"));

        var state = GameState.For(game.Root);
        var change = Ledger.Load(state, state.LoadBaseline()!).Find(data.GetProperty("changeId").GetString()!)!;
        Assert.AreEqual(ChangeKinds.Mod, change.Kind);
        Assert.AreEqual("Test mod", change.Title);
        CollectionAssert.AreEqual(new[] { "BepInEx/plugins/SpecTuner/TestMod.dll" }, change.Files);
        Assert.IsTrue(File.Exists(Path.Combine(state.Dir, "mods", "TestMod", change.Id, "Plugin.cs")));
        Assert.IsTrue(File.Exists(Path.Combine(state.Dir, "mods", "TestMod", "plugin.json")));
    }

    [TestMethod]
    public void MultipleSourceFiles_AreAccepted()
    {
        var game = CreateGame();
        var envelope = Run("build-plugin", new
        {
            gamePath = game.Root, name = "TwoFiles", title = "Two files", test = false,
            sources = new[]
            {
                new { file = "Plugin.cs", code = ModSources.Plugin("Helper.Say();") },
                new { file = "Helper.cs", code = "namespace TestMod { public static class Helper { public static void Say() { } } }" },
            },
        });
        AssertOk(envelope);
    }

    [TestMethod]
    public void Rebuild_IsANewChange_AndUndoBringsBackThePreviousDll()
    {
        var game = CreateGame();
        AssertOk(Build(game, ModSources.Plugin(version: "1.0.0")));
        var firstDll = File.ReadAllBytes(game.Full("BepInEx/plugins/SpecTuner/TestMod.dll"));

        var second = Build(game, ModSources.Plugin(version: "1.0.1"), title: "Test mod v2");

        AssertOk(second);
        Assert.IsTrue(second.GetProperty("data").GetProperty("replaces").GetBoolean());
        CollectionAssert.AreNotEqual(firstDll, File.ReadAllBytes(game.Full("BepInEx/plugins/SpecTuner/TestMod.dll")));

        AssertOk(Run("revert", new { gamePath = game.Root, changeId = second.GetProperty("data").GetProperty("changeId").GetString() }));
        CollectionAssert.AreEqual(firstDll, File.ReadAllBytes(game.Full("BepInEx/plugins/SpecTuner/TestMod.dll")));
    }

    [TestMethod]
    public void CompileErrors_AreCapped_WithHints()
    {
        var game = CreateGame();
        var many = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"int x{i} = \"text\";"));
        var source = ModSources.Plugin(body: "var s = new Secret(); var v = s.hidden; NoSuchType t = null;\n" + many,
            extraTypes: "public class Secret { private int hidden = 1; }");

        var envelope = Build(game, source);

        AssertRefused(envelope, "COMPILE_FAILED");
        var data = envelope.GetProperty("data");
        Assert.AreEqual(22, data.GetProperty("errorCount").GetInt32());
        Assert.AreEqual(PluginCompiler.MaxErrors, data.GetProperty("errors").GetArrayLength());
        var errors = data.GetProperty("errors").EnumerateArray().ToList();
        var privateAccess = errors.Single(e => e.GetProperty("id").GetString() == "CS0122");
        StringAssert.Contains(privateAccess.GetProperty("hint").GetString()!, "AccessTools");
        var unknownType = errors.Single(e => e.GetProperty("id").GetString() == "CS0246");
        StringAssert.Contains(unknownType.GetProperty("hint").GetString()!, "search-code");
        Assert.IsGreaterThan(0, errors[0].GetProperty("line").GetInt32());
        StringAssert.Contains(envelope.GetProperty("error").GetProperty("message").GetString()!, "Hint:");
        Assert.IsFalse(game.Exists("BepInEx"), "nothing is installed when the mod does not compile");
    }

    [TestMethod]
    public void NoPluginClass_IsInvalidPlugin()
    {
        var game = CreateGame();
        var envelope = Build(game, "namespace X { public class NotAPlugin { } }");
        AssertRefused(envelope, "INVALID_PLUGIN");
        StringAssert.Contains(envelope.GetProperty("error").GetProperty("message").GetString()!, "[BepInPlugin");
    }

    [TestMethod]
    public void TwoPluginClasses_IsInvalidPlugin()
    {
        var game = CreateGame();
        var second = """
            [BepInPlugin("other.mod", "Other", "1.0.0")]
            public class Other : BaseUnityPlugin { }
            """;
        AssertRefused(Build(game, ModSources.Plugin(extraTypes: second)), "INVALID_PLUGIN");
    }

    [TestMethod]
    public void PluginAttributeOnAClassThatIsNotAPlugin_IsInvalidPlugin()
    {
        var game = CreateGame();
        var source = """
            using BepInEx;
            [BepInPlugin("a.b", "A", "1.0.0")]
            public class NotDerived { }
            """;
        AssertRefused(Build(game, source), "INVALID_PLUGIN");
    }

    [TestMethod]
    public void ProbeGuid_IsRefused()
    {
        var game = CreateGame();
        AssertRefused(Build(game, ModSources.Plugin(guid: "com.spectuner.probe")), "INVALID_PLUGIN");
    }

    [TestMethod]
    [DataRow("Bad Name")]
    [DataRow("1abc")]
    [DataRow("SpecTuner")]
    public void BadNames_AreRefused(string name)
    {
        var game = CreateGame();
        AssertRefused(Build(game, ModSources.Plugin(), name: name), "BAD_REQUEST");
    }

    [TestMethod]
    public void UnsafeCode_ThroughAnAlias_OrFullyQualified_IsRefused_AndNothingIsWritten()
    {
        var game = CreateGame();
        var before = game.Hashes();

        var alias = Build(game, ModSources.Plugin(body: "P.Start(\"calc.exe\");", usings: "using P = System.Diagnostics.Process;"));
        var qualified = Build(game, ModSources.Plugin(body: "global::System.IO.File.WriteAllText(\"x.txt\", \"x\");"));

        AssertRefused(alias, "UNSAFE_CODE");
        StringAssert.Contains(alias.GetProperty("error").GetProperty("message").GetString()!, "System.Diagnostics.Process");
        StringAssert.Contains(alias.GetProperty("error").GetProperty("message").GetString()!, "must not start other programs");
        AssertRefused(qualified, "UNSAFE_CODE");
        var refused = qualified.GetProperty("data").GetProperty("refused")[0];
        Assert.AreEqual("file access", refused.GetProperty("category").GetString());
        Assert.AreEqual("System.IO.File.WriteAllText", refused.GetProperty("construct").GetString());
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void TestLaunch_ShowsWhetherEachModLoaded_AndItsErrors()
    {
        var game = CreateGame().UseFakeGameExe();
        AssertOk(Build(game, ModSources.Plugin(), name: "TestMod"));
        AssertOk(Build(game, ModSources.Plugin(guid: "quiet.mod", name: "Quiet Mod", ns: "QuietMod"), name: "QuietMod"));
        // This test is about launch-and-benchmark's report, so the builds skip their own test launch.
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", "result");
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_LOG_EXTRA", string.Join("\n",
            "[Info   :   BepInEx] Loading [Test Mod 1.0.0]",
            "[Error  : Unity Log] HarmonyException: Patching exception in method null",
            "Stack trace:",
            "TestMod.Plugin.Awake () (at <abc>:0)",
            "[Info   :  Test Mod] loaded",
            "[Error  :  Test Mod] could not find the player"));
        try
        {
            var data = Run("launch-and-benchmark", new { gamePath = game.Root, warmupSeconds = 0, measureSeconds = 1, startupTimeoutSeconds = 30 })
                .GetProperty("data");

            var mods = data.GetProperty("mods").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!);
            Assert.IsTrue(mods["TestMod"].GetProperty("loaded").GetBoolean());
            Assert.IsFalse(mods["QuietMod"].GetProperty("loaded").GetBoolean(), "not in loadedPlugins");
            var errors = data.GetProperty("pluginErrors").GetProperty("Test Mod").EnumerateArray().Select(e => e.GetString()!).ToList();
            Assert.HasCount(2, errors);
            StringAssert.Contains(errors[0], "HarmonyException");
            StringAssert.Contains(errors[0], "TestMod.Plugin.Awake");
            StringAssert.Contains(errors[1], "could not find the player");
            Assert.IsFalse(data.GetProperty("pluginErrors").TryGetProperty("Quiet Mod", out _));

            var logs = Run("read-logs", new { gamePath = game.Root, filter = "Test Mod" }).GetProperty("data");
            Assert.AreEqual(3, logs.GetProperty("lines").GetArrayLength());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", null);
            Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_LOG_EXTRA", null);
        }
    }

    [TestMethod]
    public void ReflectionAndHarmony_AreAllowed()
    {
        var game = CreateGame();
        var patch = """
            public class Target { private float speed = 1f; public float Run() { return speed; } }

            [HarmonyPatch(typeof(Target), "Run")]
            public static class RunPatch
            {
                private static readonly System.Reflection.FieldInfo Speed = AccessTools.Field(typeof(Target), "speed");
                private static bool logged;

                private static void Postfix(Target __instance, ref float __result, float ___speed)
                {
                    var viaReflection = (float)typeof(Target).GetField("speed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(__instance);
                    var viaTraverse = Traverse.Create(__instance).Field("speed").GetValue<float>();
                    var viaAccessTools = (float)Speed.GetValue(__instance);
                    if (!logged) { logged = true; BepInEx.Logging.Logger.CreateLogSource("Test").LogInfo(viaReflection + viaTraverse + viaAccessTools + ___speed); }
                    __result *= 2f;
                }
            }
            """;
        var body = "var methods = Harmony.GetAllPatchedMethods(); var path = System.IO.Path.Combine(\"a\", \"b\"); var watch = System.Diagnostics.Stopwatch.StartNew();";

        var envelope = Build(game, ModSources.Plugin(body: body, extraTypes: patch));

        AssertOk(envelope);
    }
}
