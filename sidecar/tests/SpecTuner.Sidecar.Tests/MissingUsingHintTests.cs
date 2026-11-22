using System.Text.Json;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

/// <summary>Compile hints that name the exact using for well-known types.</summary>
[TestClass]
public sealed class MissingUsingHintTests : SidecarTest
{
    private static List<JsonElement> Errors(GameFixture game, string body)
    {
        var envelope = Run("build-plugin", new { gamePath = game.Root, name = "Hinted", title = "Hinted", test = false, source = ModSources.Plugin(body: body) });
        AssertRefused(envelope, "COMPILE_FAILED");
        return envelope.GetProperty("data").GetProperty("errors").EnumerateArray().ToList();
    }

    private static string? Hint(JsonElement error) => error.TryGetProperty("hint", out var hint) ? hint.GetString() : null;

    [TestMethod]
    [DataRow("var s = new HashSet<int>();", "System.Collections.Generic")]
    [DataRow("var l = new List<string>();", "System.Collections.Generic")]
    [DataRow("var d = new Dictionary<string, int>();", "System.Collections.Generic")]
    [DataRow("var q = new Queue<int>();", "System.Collections.Generic")]
    [DataRow("var s = new Stack<int>();", "System.Collections.Generic")]
    [DataRow("IEnumerable<int> e = null;", "System.Collections.Generic")]
    [DataRow("IEnumerator e = null;", "System.Collections")]
    [DataRow("var r = Enumerable.Range(0, 3);", "System.Linq")]
    [DataRow("var a = new int[] { 1, 2 }; var w = a.Where(x => x > 1);", "System.Linq")]
    [DataRow("var sb = new StringBuilder();", "System.Text")]
    [DataRow("Action a = null;", "System")]
    [DataRow("Func<int> f = null;", "System")]
    [DataRow("var m = Math.Abs(-1);", "System")]
    [DataRow("Exception ex = null;", "System")]
    [DataRow("Type t = null;", "System")]
    [DataRow("MethodInfo m = null;", "System.Reflection")]
    [DataRow("FieldInfo f = null;", "System.Reflection")]
    [DataRow("PropertyInfo p = null;", "System.Reflection")]
    [DataRow("var b = BindingFlags.Public;", "System.Reflection")]
    [DataRow("ManualLogSource log = null;", "BepInEx.Logging")]
    public void UnknownWellKnownType_NamesTheExactUsing(string body, string ns)
    {
        var game = CreateGame();

        var errors = Errors(game, body);

        var hint = Hint(errors[0]);
        Assert.IsNotNull(hint, errors[0].ToString());
        StringAssert.Contains(hint, $"using {ns};");
    }

    [TestMethod]
    [DataRow("var k = Keyboard.current;")]
    [DataRow("var m = Mouse.current;")]
    [DataRow("var g = Gamepad.current;")]
    public void NewInputSystem_NamesItsNamespace_AndSaysWhenTheGameLacksIt(string body)
    {
        var game = CreateGame(); // the fixture game has no Unity.InputSystem

        var hint = Hint(Errors(game, body)[0])!;

        StringAssert.Contains(hint, "UnityEngine.InputSystem");
        StringAssert.Contains(hint, "does not include");
        StringAssert.Contains(hint, "Input.GetKeyDown");
    }

    [TestMethod]
    public void OtherUnknownTypes_KeepTheGenericHint()
    {
        var game = CreateGame();

        var hint = Hint(Errors(game, "NoSuchThing t = null;")[0])!;

        StringAssert.Contains(hint, "search-code");
    }

    private static string? LogHint(GameFixture game, string pluginBody)
    {
        var source = """
            using BepInEx;
            using BepInEx.Logging;
            namespace SpecTunerMods.T
            {
                [BepInPlugin("spectuner.mods.t", "T Mod", "1.0.0")]
                public class TMod : BaseUnityPlugin
                {
                    PLUGIN_BODY
                }
                static class Patch
                {
                    public static void Run() { Log.LogInfo("x"); }
                }
            }
            """.Replace("PLUGIN_BODY", pluginBody);
        var envelope = Run("build-plugin", new { gamePath = game.Root, name = "Hinted", title = "Hinted", test = false, source });
        AssertRefused(envelope, "COMPILE_FAILED");
        return Hint(envelope.GetProperty("data").GetProperty("errors")[0]);
    }

    [TestMethod]
    public void LogUsedInAPatchClass_NamesThePluginClassField()
    {
        var hint = LogHint(CreateGame(), "internal static ManualLogSource Log; void Awake() { Log = Logger; }");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "TMod.Log");
    }

    [TestMethod]
    public void LogUsedInAPatchClass_WithNoStaticLogger_SaysHowToAddOne()
    {
        var hint = LogHint(CreateGame(), "void Awake() { }");

        Assert.IsNotNull(hint);
        StringAssert.Contains(hint, "internal static ManualLogSource Log;");
        StringAssert.Contains(hint, "TMod.Log");
    }
}
