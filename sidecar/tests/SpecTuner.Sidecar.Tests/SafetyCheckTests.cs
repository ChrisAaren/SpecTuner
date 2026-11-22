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
        list.Add(MetadataReference.CreateFromImage(MonoStub.Assembly));
        // As PluginCompiler.GameReferences does: 0Harmony20.dll duplicates every Harmony type, which would leave Harmony names unresolved.
        list.AddRange(BepInExPackage.CoreImages()
            .Where(c => !c.Name.EndsWith("/0Harmony20.dll", StringComparison.OrdinalIgnoreCase))
            .Select(c => (MetadataReference)MetadataReference.CreateFromImage(c.Image)));
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

        ["MonoMod.Utils.DynDll"] = "var lib = MonoMod.Utils.DynDll.OpenLibrary(\"libc\");",
        ["MonoMod.Utils.DynDllImportAttribute"] = "var t = typeof(MonoMod.Utils.DynDllImportAttribute);",
        ["MonoMod.Utils.DynDllMapping"] = "var t = typeof(MonoMod.Utils.DynDllMapping);",
        ["MonoMod.RuntimeDetour.NativeDetour"] = "var d = new MonoMod.RuntimeDetour.NativeDetour(System.IntPtr.Zero, System.IntPtr.Zero);",
        ["MonoMod.RuntimeDetour.NativeDetourData"] = "var t = typeof(MonoMod.RuntimeDetour.NativeDetourData);",
        ["MonoMod.RuntimeDetour.DetourHelper"] = "MonoMod.RuntimeDetour.DetourHelper.MakeWritable(System.IntPtr.Zero, 4u);",
        ["MonoMod.RuntimeDetour.IDetourNativePlatform"] = "MonoMod.RuntimeDetour.IDetourNativePlatform p = null;",
        ["MonoMod.RuntimeDetour.Platforms.*"] = "var p = new MonoMod.RuntimeDetour.Platforms.DetourNativeX86Platform();",
        ["HarmonyLib.Public.Patching.NativeDetourMethodPatcher"] = "var t = typeof(HarmonyLib.Public.Patching.NativeDetourMethodPatcher);",
        ["BepInEx.Unix.*"] = "var t = typeof(BepInEx.Unix.UnixStreamHelper.fwriteDelegate);",
        ["System.Runtime.CompilerServices.Unsafe"] = "int x = 0; System.Runtime.CompilerServices.Unsafe.As<int, long>(ref x) = 1;",
        ["System.Type.GetTypeFromProgID"] = "var t = System.Type.GetTypeFromProgID(\"WScript.Shell\");",
        ["System.Type.GetTypeFromCLSID"] = "var t = System.Type.GetTypeFromCLSID(System.Guid.Empty);",
        ["Mono.Unix.*"] = "Mono.Unix.Native.Syscall.chmod(\"x\", 511u);",
        ["Mono.Posix.*"] = "Mono.Posix.Syscall.unlink(\"x\");",
        ["System.Reflection.Assembly.LoadWithPartialName"] = "System.Reflection.Assembly.LoadWithPartialName(\"x\");",
        ["MonoMod.Utils.DynamicMethodDefinition"] = "var d = new MonoMod.Utils.DynamicMethodDefinition(\"x\", typeof(void), new System.Type[0]);",
        ["MonoMod.Utils.DMDGenerator"] = "var t = typeof(MonoMod.Utils.DMDGenerator<MonoMod.Utils.DMDEmitDynamicMethodGenerator>);",
        ["MonoMod.Utils.DMDEmitDynamicMethodGenerator"] = "var g = new MonoMod.Utils.DMDEmitDynamicMethodGenerator();",
        ["MonoMod.Utils.DMDEmitMethodBuilderGenerator"] = "var g = new MonoMod.Utils.DMDEmitMethodBuilderGenerator();",
        ["MonoMod.Utils.DMDCecilGenerator"] = "var g = new MonoMod.Utils.DMDCecilGenerator();",
        ["MonoMod.Utils.Cil.*"] = "var t = typeof(MonoMod.Utils.Cil.ILGeneratorShim);",
        ["MonoMod.Utils.ReflectionHelper.Load"] = "MonoMod.Utils.ReflectionHelper.Load((Mono.Cecil.ModuleDefinition)null);",
        ["Mono.CSharp.*"] = "new Mono.CSharp.Evaluator().Run(\"1\");",
        ["BepInEx.Preloader.*"] = "var t = typeof(BepInEx.Preloader.Patching.AssemblyPatcher);",
        ["BepInEx.Bootstrap.TypeLoader"] = "var t = typeof(BepInEx.Bootstrap.TypeLoader);",
        ["BepInEx.Bootstrap.Chainloader.Initialize"] = "BepInEx.Bootstrap.Chainloader.Initialize(\"x\", true, null);",
        ["BepInEx.Bootstrap.Chainloader.Start"] = "BepInEx.Bootstrap.Chainloader.Start();",
        ["BepInEx.Utility.TryResolveDllAssembly"] = "System.Reflection.Assembly a; BepInEx.Utility.TryResolveDllAssembly(new System.Reflection.AssemblyName(\"x\"), \"dir\", out a);",
        ["HarmonyXInterop.*"] = "var t = typeof(HarmonyXInterop.HarmonyInterop);",
        ["System.Xml.Xsl.*"] = "var x = new System.Xml.Xsl.XslCompiledTransform();",
        ["System.IO.Compression.ZipFile"] = "System.IO.Compression.ZipFile.ExtractToDirectory(\"a.zip\", \"dir\");",
        ["System.IO.Compression.ZipFileExtensions"] = "System.IO.Compression.ZipFileExtensions.ExtractToDirectory(null, \"dir\");",
        ["System.IO.Pipes.*"] = "var p = new System.IO.Pipes.NamedPipeClientStream(\"x\");",
        ["System.IO.MemoryMappedFiles.*"] = "var m = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(\"x\");",
        ["System.IO.IsolatedStorage.*"] = "var s = System.IO.IsolatedStorage.IsolatedStorageFile.GetUserStoreForAssembly();",
        ["System.IO.Ports.*"] = "var s = new System.IO.Ports.SerialPort(\"COM1\");",
        ["System.Xml.XmlDocument.Load"] = "new System.Xml.XmlDocument().Load(\"x.xml\");",
        ["System.Xml.XmlDocument.Save"] = "new System.Xml.XmlDocument().Save(\"x.xml\");",
        ["System.Xml.XmlReader.Create"] = "var r = System.Xml.XmlReader.Create(\"x.xml\");",
        ["System.Xml.XmlWriter.Create"] = "var w = System.Xml.XmlWriter.Create(\"x.xml\");",
        ["System.Xml.XmlTextReader"] = "var r = new System.Xml.XmlTextReader(\"x.xml\");",
        ["System.Xml.XmlTextWriter"] = "var w = new System.Xml.XmlTextWriter(\"x.xml\", null);",
        ["System.Xml.XmlUrlResolver"] = "var r = new System.Xml.XmlUrlResolver();",
        ["System.Xml.Linq.XDocument.Load"] = "var d = System.Xml.Linq.XDocument.Load(\"x.xml\");",
        ["System.Xml.Linq.XDocument.Save"] = "new System.Xml.Linq.XDocument().Save(\"x.xml\");",
        ["System.Xml.Linq.XElement.Load"] = "var e = System.Xml.Linq.XElement.Load(\"x.xml\");",
        ["System.Xml.Linq.XElement.Save"] = "new System.Xml.Linq.XElement(\"a\").Save(\"x.xml\");",
        ["System.Resources.ResourceReader"] = "var r = new System.Resources.ResourceReader(\"x.resources\");",
        ["System.Resources.ResourceWriter"] = "var w = new System.Resources.ResourceWriter(\"x.resources\");",
        ["System.Resources.ResourceManager.CreateFileBasedResourceManager"] = "System.Resources.ResourceManager.CreateFileBasedResourceManager(\"x\", \"dir\", null);",
        ["System.Diagnostics.TextWriterTraceListener"] = "var l = new System.Diagnostics.TextWriterTraceListener(\"x.log\");",
        ["System.Diagnostics.DelimitedListTraceListener"] = "var l = new System.Diagnostics.DelimitedListTraceListener(\"x.log\");",
        ["System.Diagnostics.XmlWriterTraceListener"] = "var l = new System.Diagnostics.XmlWriterTraceListener(\"x.log\");",
        ["System.Diagnostics.DefaultTraceListener.LogFileName"] = "new System.Diagnostics.DefaultTraceListener().LogFileName = \"x.log\";",
        ["Mono.Cecil.AssemblyDefinition.ReadAssembly"] = "var a = Mono.Cecil.AssemblyDefinition.ReadAssembly(\"x.dll\");",
        ["Mono.Cecil.AssemblyDefinition.Write"] = "((Mono.Cecil.AssemblyDefinition)null).Write(\"x.dll\");",
        ["Mono.Cecil.ModuleDefinition.ReadModule"] = "var m = Mono.Cecil.ModuleDefinition.ReadModule(\"x.dll\");",
        ["Mono.Cecil.ModuleDefinition.Write"] = "((Mono.Cecil.ModuleDefinition)null).Write(\"x.dll\");",
        ["Mono.Cecil.DefaultAssemblyResolver"] = "var r = new Mono.Cecil.DefaultAssemblyResolver();",
        ["Mono.Cecil.BaseAssemblyResolver"] = "var t = typeof(Mono.Cecil.BaseAssemblyResolver);",
        ["Mono.Cecil.Cil.DefaultSymbolReaderProvider"] = "var p = new Mono.Cecil.Cil.DefaultSymbolReaderProvider();",
        ["Mono.Cecil.Cil.DefaultSymbolWriterProvider"] = "var p = new Mono.Cecil.Cil.DefaultSymbolWriterProvider();",
        ["Mono.Cecil.Cil.PortablePdbReaderProvider"] = "var p = new Mono.Cecil.Cil.PortablePdbReaderProvider();",
        ["Mono.Cecil.Cil.PortablePdbWriterProvider"] = "var p = new Mono.Cecil.Cil.PortablePdbWriterProvider();",
        ["Mono.Cecil.Pdb.*"] = "var p = new Mono.Cecil.Pdb.PdbReaderProvider();",
        ["Mono.Cecil.Mdb.*"] = "var p = new Mono.Cecil.Mdb.MdbReaderProvider();",
        ["Mono.Data.Sqlite.*"] = "var c = new Mono.Data.Sqlite.SqliteConnection(\"Data Source=x.db\");",
        ["HarmonyLib.FileLog"] = "HarmonyLib.FileLog.Log(\"x\");",
        ["HarmonyLib.Tools.HarmonyFileLog"] = "HarmonyLib.Tools.HarmonyFileLog.Enabled = true;",
        ["HarmonyLib.Harmony.DEBUG"] = "HarmonyLib.Harmony.DEBUG = true;",
        ["HarmonyLib.HarmonyDebug"] = "member:[HarmonyLib.HarmonyDebug] public static void Patched() { }",
        ["BepInEx.Configuration.ConfigFile..ctor"] = "var c = new BepInEx.Configuration.ConfigFile(\"x.cfg\", true);",
        ["BepInEx.Logging.DiskLogListener"] = "var l = new BepInEx.Logging.DiskLogListener(\"x.log\");",
        ["BepInEx.Utility.TryOpenFileStream"] = "System.IO.FileStream s; BepInEx.Utility.TryOpenFileStream(\"x\", System.IO.FileMode.Create, out s);",
        ["UnityEngine.Windows.File"] = "UnityEngine.Windows.File.ReadAllBytes(\"x\");",
        ["UnityEngine.Windows.Directory"] = "UnityEngine.Windows.Directory.CreateDirectory(\"x\");",
        ["UnityEngine.ScreenCapture.CaptureScreenshot"] = "UnityEngine.ScreenCapture.CaptureScreenshot(\"x.png\");",
        ["UnityEngine.Application.CaptureScreenshot"] = "UnityEngine.Application.CaptureScreenshot(\"x.png\");",
        ["UnityEngine.AssetBundle.LoadFromFile"] = "UnityEngine.AssetBundle.LoadFromFile(\"x\");",
        ["UnityEngine.AssetBundle.LoadFromFileAsync"] = "UnityEngine.AssetBundle.LoadFromFileAsync(\"x\");",
        ["UnityEngine.Caching"] = "UnityEngine.Caching.ClearCache();",
        ["UnityEngine.Profiling.Profiler.logFile"] = "UnityEngine.Profiling.Profiler.logFile = \"x\";",
        ["System.Runtime.Remoting.*"] = "System.Runtime.Remoting.RemotingConfiguration.Configure(\"x\");",
        ["UnityEngine.Ping"] = "var p = new UnityEngine.Ping(\"127.0.0.1\");",
        ["UnityEngine.Network"] = "UnityEngine.Network.Disconnect();",
        ["UnityEngine.NetworkView"] = "var t = typeof(UnityEngine.NetworkView);",
        ["UnityEngine.MasterServer"] = "UnityEngine.MasterServer.ClearHostList();",
        ["System.Diagnostics.Debugger.Launch"] = "System.Diagnostics.Debugger.Launch();",
        ["UnityEngine.Diagnostics.Utils"] = "UnityEngine.Diagnostics.Utils.ForceCrash(0);",
        ["System.Threading.Tasks.Parallel"] = "System.Threading.Tasks.Parallel.For(0, 2, i => { });",
        ["System.Threading.Tasks.Task.Start"] = "new System.Threading.Tasks.Task(() => { }).Start();",
        ["System.ComponentModel.BackgroundWorker"] = "var w = new System.ComponentModel.BackgroundWorker();",
        ["BepInEx.ThreadingHelper.StartAsyncInvoke"] = "BepInEx.ThreadingHelper.Instance.StartAsyncInvoke(() => null);",
        ["BepInEx.ThreadingExtensions.RunParallel"] = "BepInEx.ThreadingExtensions.RunParallel(new int[0], i => { });",
        ["UnityEngine.WebCamTexture"] = "var t = typeof(UnityEngine.WebCamTexture);",
        ["UnityEngine.Microphone"] = "var d = UnityEngine.Microphone.devices;",
        ["UnityEngine.Windows.WebCam.*"] = "var t = typeof(UnityEngine.Windows.WebCam.PhotoCapture);",
        ["UnityEngine.Windows.Speech.*"] = "var t = typeof(UnityEngine.Windows.Speech.DictationRecognizer);",
        ["UnityEngine.Application.RequestUserAuthorization"] = "UnityEngine.Application.RequestUserAuthorization(1);",
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
    public void HarmonyPatchingAndPluginConfig_StayAllowed()
    {
        var allowed = """
            var harmony = new Harmony("test.mod");
            harmony.PatchAll();
            var original = AccessTools.Method(typeof(Plugin), "Awake");
            harmony.Patch(original, prefix: new HarmonyMethod(typeof(Plugin), "Awake"));
            var patched = Harmony.GetAllPatchedMethods();
            harmony.UnpatchSelf();
            var entry = Config.Bind("General", "Jump", 2f, "Jump height");
            Config.Save();
            var others = BepInEx.Bootstrap.Chainloader.PluginInfos;
            var root = BepInEx.Paths.GameRootPath;
            BepInEx.ThreadingHelper.Instance.StartSyncInvoke(() => { });
            """;
        var violations = Check(allowed);
        Assert.IsEmpty(violations, string.Join("; ", violations.Select(v => v.Construct)));
    }

    [TestMethod]
    public void ConstructorRule_AppliesToExplicitAndImplicitNew()
    {
        Assert.IsTrue(Check("var c = new BepInEx.Configuration.ConfigFile(\"x.cfg\", true);").Any(v => v.Construct == "BepInEx.Configuration.ConfigFile..ctor"));
        Assert.IsTrue(Check("BepInEx.Configuration.ConfigFile c = new(\"x.cfg\", true);").Any(v => v.Construct == "BepInEx.Configuration.ConfigFile..ctor"));
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
