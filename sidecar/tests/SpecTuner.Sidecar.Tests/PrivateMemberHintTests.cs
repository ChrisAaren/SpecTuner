using Microsoft.CodeAnalysis;
using SpecTuner.Sidecar.Compilation;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

/// <summary>Private members of game types, as a model would try to use them.</summary>
[TestClass]
public sealed class PrivateMemberHintTests : SidecarTest
{
    private static readonly Lazy<byte[]> GameAssembly = new(() =>
    {
        const string source = """
            namespace SampleGame
            {
                public class PlayerController
                {
                    private float jumpHeight = 1.5f;
                    private static int jumps;
                    private float Speed { get; set; }
                    private void Update() { }
                    public float Gravity = -9.8f;
                }
            }
            """;
        var references = new[] { "System.Runtime.dll", "System.Private.CoreLib.dll" }
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(UnityStub.RuntimeDirectory, f))).ToList();
        var result = PluginCompiler.Compile("Assembly-CSharp", [new SourceFile("Game.cs", source)], references);
        Assert.IsTrue(result.Success, string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Assembly!;
    });

    private GameFixture CreateSampleGame()
    {
        var game = CreateGame();
        game.Write($"{game.DataRelative}/Managed/Assembly-CSharp.dll", GameAssembly.Value);
        return game;
    }

    private static string Patch(string prefix) => $$"""
        [HarmonyPatch(typeof(SampleGame.PlayerController), "Update")]
        public static class JumpPatch
        {
            {{prefix}}
        }
        """;

    private static System.Text.Json.JsonElement Build(GameFixture game, string patch) =>
        Run("build-plugin", new { gamePath = game.Root, name = "Jumper", title = "Jumper", test = false, source = ModSources.Plugin(extraTypes: patch) });

    private static System.Text.Json.JsonElement FirstError(System.Text.Json.JsonElement envelope)
    {
        AssertRefused(envelope, "COMPILE_FAILED");
        return envelope.GetProperty("data").GetProperty("errors")[0];
    }

    [TestMethod]
    public void DirectAccessToAPrivateField_SaysItIsPrivate_AndShowsBothWays()
    {
        var game = CreateSampleGame();

        var error = FirstError(Build(game, Patch("static void Prefix(SampleGame.PlayerController __instance) { __instance.jumpHeight *= 2f; }")));

        Assert.AreEqual("CS0122", error.GetProperty("id").GetString(), "the member exists, so this must not be 'does not contain a definition'");
        var hint = error.GetProperty("hint").GetString()!;
        StringAssert.Contains(hint, "'jumpHeight' is a private field of PlayerController");
        StringAssert.Contains(hint, "ref float ___jumpHeight");
        StringAssert.Contains(hint, "AccessTools.FieldRefAccess<PlayerController, float>(\"jumpHeight\")");
    }

    [TestMethod]
    public void PrivateStaticField_PrivateMethod_AndPrivateProperty_GetSpecificHints()
    {
        var game = CreateSampleGame();

        var staticField = FirstError(Build(game, Patch("static void Prefix() { SampleGame.PlayerController.jumps++; }")));
        StringAssert.Contains(staticField.GetProperty("hint").GetString()!, "___jumps");

        var method = FirstError(Build(game, Patch("static void Prefix(SampleGame.PlayerController __instance) { __instance.Update(); }")));
        StringAssert.Contains(method.GetProperty("hint").GetString()!, "AccessTools.Method(typeof(PlayerController), \"Update\")");

        var property = FirstError(Build(game, Patch("static void Prefix(SampleGame.PlayerController __instance) { var s = __instance.Speed; }")));
        StringAssert.Contains(property.GetProperty("hint").GetString()!, "Traverse.Create(instance).Property(\"Speed\")");
    }

    [TestMethod]
    public void AMemberThatDoesNotExist_KeepsTheReadCodeHint()
    {
        var game = CreateSampleGame();

        var error = FirstError(Build(game, Patch("static void Prefix(SampleGame.PlayerController __instance) { __instance.jumpPower *= 2f; }")));

        Assert.AreEqual("CS1061", error.GetProperty("id").GetString());
        StringAssert.Contains(error.GetProperty("hint").GetString()!, "use read-code on the type");
    }

    [TestMethod]
    public void TripleUnderscoreParameter_AndAccessTools_StillBuild()
    {
        var game = CreateSampleGame();
        var patch = Patch("""
            private static readonly AccessTools.FieldRef<SampleGame.PlayerController, float> JumpHeight =
                AccessTools.FieldRefAccess<SampleGame.PlayerController, float>("jumpHeight");

            static void Prefix(SampleGame.PlayerController __instance, ref float ___jumpHeight)
            {
                ___jumpHeight *= 2f;
                JumpHeight(__instance) += 0f;
                AccessTools.Method(typeof(SampleGame.PlayerController), "Update").Invoke(__instance, null);
                __instance.Gravity *= 1f;
            }
            """);

        AssertOk(Build(game, patch));
    }
}
