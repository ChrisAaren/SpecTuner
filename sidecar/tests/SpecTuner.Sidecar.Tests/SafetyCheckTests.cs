using Microsoft.CodeAnalysis;
using SpecTuner.Sidecar.Compilation;
using SpecTuner.Sidecar.Loader;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

/// <summary>Every row of the deny table, checked against the full .NET runtime so each API resolves.</summary>
[TestClass]
public sealed class SafetyCheckTests
{
    private static readonly Lazy<List<MetadataReference>> References = new(() =>
    {
        var list = Directory.GetFiles(UnityStub.RuntimeDirectory, "*.dll")
            .Where(p => PluginCompiler.AssemblyName(File.ReadAllBytes(p)) is not null)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        list.Add(MetadataReference.CreateFromImage(UnityStub.Assembly));
        list.AddRange(BepInExPackage.CoreImages().Select(c => (MetadataReference)MetadataReference.CreateFromImage(c.Image)));
        return list;
    });

    /// <summary>Code that uses each rule's target, written inside a method body (or as a member, prefixed "member:").</summary>
    private static readonly Dictionary<string, string> Uses = new()
    {
        ["System.Diagnostics.Process"] = "System.Diagnostics.Process.Start(\"calc.exe\");",
        ["System.Diagnostics.ProcessStartInfo"] = "var p = new System.Diagnostics.ProcessStartInfo();",
        ["System.Net.*"] = "var c = new System.Net.WebClient();",
        ["UnityEngine.Networking.*"] = "UnityEngine.Networking.UnityWebRequest.Get(\"http://x\");",
        ["UnityEngine.WWW"] = "var w = new UnityEngine.WWW(\"http://x\");",
        ["UnityEngine.WWWForm"] = "var f = new UnityEngine.WWWForm();",
        ["UnityEngine.Application.OpenURL"] = "UnityEngine.Application.OpenURL(\"http://x\");",
        ["Microsoft.Win32.*"] = "Microsoft.Win32.Registry.GetValue(\"HKEY_CURRENT_USER\\\\x\", \"y\", null);",
        ["System.Runtime.InteropServices.DllImportAttribute"] = "member:[System.Runtime.InteropServices.DllImport(\"user32.dll\")] private static extern int MessageBeep(uint type);",
        ["System.Runtime.InteropServices.Marshal"] = "var ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);",
        ["System.Runtime.InteropServices.NativeLibrary"] = "System.Runtime.InteropServices.NativeLibrary.Load(\"x.dll\");",
        ["System.Reflection.Assembly.Load"] = "System.Reflection.Assembly.Load(new byte[0]);",
        ["System.Reflection.Assembly.LoadFrom"] = "System.Reflection.Assembly.LoadFrom(\"x.dll\");",
        ["System.Reflection.Assembly.LoadFile"] = "System.Reflection.Assembly.LoadFile(\"x.dll\");",
        ["System.Reflection.Assembly.UnsafeLoadFrom"] = "System.Reflection.Assembly.UnsafeLoadFrom(\"x.dll\");",
        ["System.Reflection.Assembly.ReflectionOnlyLoad"] = "System.Reflection.Assembly.ReflectionOnlyLoad(\"x\");",
        ["System.Reflection.Assembly.ReflectionOnlyLoadFrom"] = "System.Reflection.Assembly.ReflectionOnlyLoadFrom(\"x.dll\");",
        ["System.Reflection.Emit.*"] = "var op = System.Reflection.Emit.OpCodes.Ret;",
        ["System.Runtime.Loader.*"] = "var ctx = System.Runtime.Loader.AssemblyLoadContext.Default;",
        ["System.CodeDom.Compiler.*"] = "var w = new System.CodeDom.Compiler.IndentedTextWriter(null);",
        ["Microsoft.CSharp.*"] = "var t = typeof(Microsoft.CSharp.RuntimeBinder.Binder);",
        ["System.AppDomain.Load"] = "System.AppDomain.CurrentDomain.Load(new byte[0]);",
        ["System.AppDomain.ExecuteAssembly"] = "System.AppDomain.CurrentDomain.ExecuteAssembly(\"x.exe\");",
        ["System.AppDomain.ExecuteAssemblyByName"] = "System.AppDomain.CurrentDomain.ExecuteAssemblyByName(\"x\");",
        ["System.AppDomain.CreateDomain"] = "System.AppDomain.CreateDomain(\"x\");",
        ["System.AppDomain.CreateInstanceFrom"] = "System.AppDomain.CurrentDomain.CreateInstanceFrom(\"x.dll\", \"T\");",
        ["System.AppDomain.CreateInstanceFromAndUnwrap"] = "System.AppDomain.CurrentDomain.CreateInstanceFromAndUnwrap(\"x.dll\", \"T\");",
        ["System.Activator.CreateInstanceFrom"] = "System.Activator.CreateInstanceFrom(\"x.dll\", \"T\");",
        ["System.IO.File"] = "System.IO.File.ReadAllText(\"x\");",
        ["System.IO.Directory"] = "System.IO.Directory.GetFiles(\"x\");",
        ["System.IO.FileInfo"] = "var f = new System.IO.FileInfo(\"x\");",
        ["System.IO.DirectoryInfo"] = "var d = new System.IO.DirectoryInfo(\"x\");",
        ["System.IO.FileSystemInfo"] = "System.IO.FileSystemInfo info = null;",
        ["System.IO.FileStream"] = "var s = new System.IO.FileStream(\"x\", System.IO.FileMode.Open);",
        ["System.IO.StreamWriter"] = "var w = new System.IO.StreamWriter(\"x\");",
        ["System.IO.StreamReader"] = "var r = new System.IO.StreamReader(\"x\");",
        ["System.IO.FileSystemWatcher"] = "var w = new System.IO.FileSystemWatcher();",
        ["System.IO.DriveInfo"] = "var d = System.IO.DriveInfo.GetDrives();",
        ["System.Environment.Exit"] = "System.Environment.Exit(0);",
        ["System.Environment.FailFast"] = "System.Environment.FailFast(\"x\");",
        ["System.Environment.SetEnvironmentVariable"] = "System.Environment.SetEnvironmentVariable(\"A\", \"B\");",
        ["System.Threading.Thread"] = "var t = new System.Threading.Thread(() => { });",
        ["System.Threading.ThreadPool"] = "System.Threading.ThreadPool.QueueUserWorkItem(_ => { });",
        ["System.Threading.Timer"] = "var t = new System.Threading.Timer(_ => { });",
        ["System.Timers.Timer"] = "var t = new System.Timers.Timer();",
        ["System.Threading.Tasks.Task.Run"] = "System.Threading.Tasks.Task.Run(() => { });",
        ["System.Threading.Tasks.TaskFactory.StartNew"] = "System.Threading.Tasks.Task.Factory.StartNew(() => { });",
    };

    private static List<SafetyViolation> Check(string bodyOrMember, string usings = "")
    {
        var member = bodyOrMember.StartsWith("member:", StringComparison.Ordinal);
        var code = member
            ? ModSources.Plugin(usings: usings, extraTypes: "public static class Extra { " + bodyOrMember["member:".Length..] + " }")
            : ModSources.Plugin(body: bodyOrMember, usings: usings);
        var compilation = PluginCompiler.CreateCompilation("Check", [new SourceFile("Plugin.cs", code)], References.Value);
        return SafetyCheck.Check(compilation);
    }

    [TestMethod]
    public void EveryRuleHasATestUse()
    {
        var missing = SafetyCheck.Rules.Select(r => r.Target).Where(t => !Uses.ContainsKey(t)).ToList();
        Assert.IsEmpty(missing, "Rules without a test: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void EveryRule_IsRefused_WithItsReason()
    {
        var failures = new List<string>();
        foreach (var rule in SafetyCheck.Rules)
        {
            var violations = Check(Uses[rule.Target]);
            var prefix = rule.Target.TrimEnd('*').TrimEnd('.');
            var hit = violations.FirstOrDefault(v => v.Construct.StartsWith(prefix, StringComparison.Ordinal) && v.Reason == rule.Reason);
            if (hit is null)
                failures.Add($"{rule.Target}: got [{string.Join("; ", violations.Select(v => v.Construct))}]");
        }
        Assert.IsEmpty(failures, string.Join("\n", failures));
    }

    [TestMethod]
    public void UnsafeExternAndDynamic_AreRefused()
    {
        Assert.IsTrue(Check("unsafe { int x = 1; int* p = &x; }").Any(v => v.Construct == "unsafe"));
        Assert.IsTrue(Check("member:[System.Runtime.InteropServices.DllImport(\"x\")] static extern void F();").Any(v => v.Construct == "extern"));
        Assert.IsTrue(Check("dynamic d = 1; d.Foo();").Any(v => v.Construct == "dynamic"));
    }

    [TestMethod]
    public void AliasesAndQualifiedNames_AreResolved()
    {
        Assert.IsTrue(Check("P.Start(\"x\");", "using P = System.Diagnostics.Process;").Any(v => v.Construct.StartsWith("System.Diagnostics.Process")));
        Assert.IsTrue(Check("F.Delete(\"x\");", "using F = System.IO.File;").Any(v => v.Construct == "System.IO.File.Delete"));
        Assert.IsTrue(Check("Delete(\"x\");", "using static System.IO.File;").Any(v => v.Construct == "System.IO.File.Delete"));
        Assert.IsTrue(Check("global::System.Environment.Exit(1);").Any(v => v.Construct == "System.Environment.Exit"));
    }

    [TestMethod]
    public void AllowedCode_PassesTheCheck()
    {
        var allowed = """
            var field = typeof(Plugin).GetField("x", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var method = AccessTools.Method(typeof(Plugin), "Awake");
            var value = Traverse.Create(this).Field("x").GetValue<int>();
            var path = System.IO.Path.Combine("a", "b");
            var ms = new System.IO.MemoryStream();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var task = System.Threading.Tasks.Task.FromResult(1);
            var name = System.Environment.MachineName;
            var list = new System.Collections.Generic.List<int>();
            UnityEngine.Application.Quit();
            """;
        var violations = Check(allowed);
        Assert.IsEmpty(violations, string.Join("; ", violations.Select(v => v.Construct)));
    }
}
