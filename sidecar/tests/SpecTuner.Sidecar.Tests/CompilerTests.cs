using System.Reflection;
using Microsoft.CodeAnalysis;
using SpecTuner.Sidecar.Compilation;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class CompilerTests : SidecarTest
{
    private static List<MetadataReference> RuntimeReferences() =>
        new[] { "System.Runtime.dll", "System.Private.CoreLib.dll" }
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(UnityStub.RuntimeDirectory, f)))
            .ToList();

    [TestMethod]
    public void ValidSource_CompilesToALoadableAssembly()
    {
        var result = PluginCompiler.Compile("Adder",
            [new SourceFile("Adder.cs", "public static class Adder { public static int Add(int a, int b) => a + b; }")],
            RuntimeReferences());

        Assert.IsTrue(result.Success, string.Join("; ", result.Errors.Select(e => e.Message)));
        var assembly = Assembly.Load(result.Assembly!);
        var sum = assembly.GetType("Adder")!.GetMethod("Add")!.Invoke(null, [2, 3]);
        Assert.AreEqual(5, sum);
    }

    [TestMethod]
    public void Errors_HaveFileLineColumnAndId()
    {
        var result = PluginCompiler.Compile("Broken",
            [new SourceFile("Broken.cs", "public class Broken\n{\n    public int X() { return \"text\"; }\n}")],
            RuntimeReferences());

        Assert.IsFalse(result.Success);
        Assert.AreEqual(1, result.ErrorCount);
        var error = result.Errors[0];
        Assert.AreEqual("CS0029", error.Id);
        Assert.AreEqual("Broken.cs", error.File);
        Assert.AreEqual(3, error.Line);
        Assert.AreEqual(29, error.Column);
    }

    [TestMethod]
    public void ManyErrors_AreCappedButCounted()
    {
        var body = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"    int F{i}() {{ return \"x\"; }}"));
        var result = PluginCompiler.Compile("Many", [new SourceFile("Many.cs", "class Many\n{\n" + body + "\n}")], RuntimeReferences());

        Assert.AreEqual(40, result.ErrorCount);
        Assert.HasCount(PluginCompiler.MaxErrors, result.Errors);
    }

    [TestMethod]
    public void LanguageVersion_IsCSharp9()
    {
        // Records and init accessors (C# 9) work, using the IsExternalInit polyfill.
        var ok = PluginCompiler.Compile("Nine", [new SourceFile("Nine.cs", "public record Point(int X, int Y);")], RuntimeReferences());
        Assert.IsTrue(ok.Success, string.Join("; ", ok.Errors.Select(e => e.Message)));

        // File-scoped namespaces are C# 10 and must be rejected.
        var ten = PluginCompiler.Compile("Ten", [new SourceFile("Ten.cs", "namespace Foo;\npublic class Bar { }")], RuntimeReferences());
        Assert.IsFalse(ten.Success);
    }

    [TestMethod]
    public void GameReferences_SkipNativeDlls_AndKeepOneCopyPerAssemblyName()
    {
        var game = CreateGame();
        game.Write($"{game.Name}_Data/Managed/native_plugin.dll", PeBuilder.Build());
        game.Write($"{game.Name}_Data/Managed/Copy-Of-Assembly-CSharp.dll", GameFixture.ManagedAssembly());
        var facts = GameInspector.Inspect(game.Root).Facts;

        var references = PluginCompiler.GameReferences(facts);

        var names = references.Select(r => Path.GetFileName(r.Display!)).ToList();
        CollectionAssert.DoesNotContain(names, "native_plugin.dll");
        Assert.AreEqual(1, names.Count(n => n is "Assembly-CSharp.dll" or "Copy-Of-Assembly-CSharp.dll"));
        CollectionAssert.Contains(names, "UnityEngine.dll");
    }
}
