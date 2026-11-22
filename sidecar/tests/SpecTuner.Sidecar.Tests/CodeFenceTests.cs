using SpecTuner.Sidecar.Compilation;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class CodeFenceTests : SidecarTest
{
    private const string Fence = "```";

    [TestMethod]
    [DataRow("```csharp")]
    [DataRow("```cs")]
    [DataRow("```")]
    [DataRow("``` C#")]
    public void EnclosingFence_IsBlanked_KeepingLineNumbers(string opening)
    {
        var text = $"\n  {opening}\nline one\nline two\n{Fence}  \n";

        var result = CodeFence.Strip(text, out var stripped);

        Assert.IsTrue(stripped);
        var lines = result.Split('\n');
        Assert.HasCount(text.Split('\n').Length, lines);
        Assert.AreEqual("", lines[1]);
        Assert.AreEqual("line one", lines[2]);
        Assert.AreEqual("", lines[4]);
        Assert.DoesNotContain(Fence, result);
    }

    [TestMethod]
    public void WindowsLineEndings_AreKept()
    {
        var result = CodeFence.Strip($"{Fence}csharp\r\nclass A {{ }}\r\n{Fence}\r\n", out var stripped);
        Assert.IsTrue(stripped);
        Assert.AreEqual("\r\nclass A { }\r\n\r\n", result);
    }

    [TestMethod]
    public void UnfencedSource_IsUnchanged()
    {
        var text = "using BepInEx;\nclass A { }\n";
        Assert.AreSame(text, CodeFence.Strip(text, out var stripped));
        Assert.IsFalse(stripped);
    }

    [TestMethod]
    public void FencesInsideTheCode_AreNotTouched()
    {
        var text = $"class A\n{{\n    // {Fence}csharp in a comment\n    string s = \"{Fence}\";\n}}\n";
        Assert.AreSame(text, CodeFence.Strip(text, out var stripped));
        Assert.IsFalse(stripped);

        var fenceLineInside = $"class A\n{{\n/*\n{Fence}\n*/\n}}";
        Assert.AreSame(fenceLineInside, CodeFence.Strip(fenceLineInside, out stripped));
        Assert.IsFalse(stripped);
    }

    [TestMethod]
    public void OpeningFenceWithoutClosing_IsNotStripped()
    {
        var text = $"{Fence}csharp\nclass A {{ }}\n";
        Assert.AreSame(text, CodeFence.Strip(text, out var stripped));
        Assert.IsFalse(stripped);
        Assert.AreEqual($"class A {{ }}\n{Fence}", CodeFence.Strip($"class A {{ }}\n{Fence}", out stripped), "closing fence alone");
        Assert.IsFalse(stripped);
    }

    [TestMethod]
    public void FencedSource_Builds_AndErrorsUseTheModelsLineNumbers()
    {
        var game = CreateGame();
        var fenced = $"{Fence}csharp\n{ModSources.Plugin()}\n{Fence}";

        var ok = Run("build-plugin", new { gamePath = game.Root, name = "Fenced", title = "Fenced", source = fenced, test = false });

        AssertOk(ok);
        Assert.AreEqual("Plugin.cs", ok.GetProperty("data").GetProperty("strippedCodeFence")[0].GetString());

        // An error on the line the model sees as line 16 is reported as line 16.
        var broken = fenced.Split('\n').ToList();
        var awake = broken.FindIndex(l => l.Contains("Logger.LogInfo(\"loaded\");"));
        broken[awake] = "int x = \"text\";";
        var failed = Run("build-plugin", new { gamePath = game.Root, name = "Fenced", title = "Fenced", test = false, sources = new[] { new { file = "Plugin.cs", code = string.Join("\n", broken) } } });

        AssertRefused(failed, "COMPILE_FAILED");
        Assert.AreEqual(awake + 1, failed.GetProperty("data").GetProperty("errors")[0].GetProperty("line").GetInt32());
    }
}
