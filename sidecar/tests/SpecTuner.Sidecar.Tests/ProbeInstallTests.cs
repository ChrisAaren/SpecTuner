using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Loader;
using SpecTuner.Sidecar.State;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class ProbeInstallTests : SidecarTest
{
    [TestMethod]
    public void ProbeSource_CompilesAgainstAGamesAssemblies()
    {
        var game = CreateGame();

        // BepInEx is not on disk yet: its core dlls come from the embedded package.
        var bytes = ProbeInstaller.Build(GameInspector.Inspect(game.Root).Facts);

        Assert.IsGreaterThan(1000, bytes.Length);
    }

    [TestMethod]
    public void Install_AddsTheProbeAsItsOwnChange()
    {
        var game = CreateGame();

        var install = Run("install", new { gamePath = game.Root });

        AssertOk(install);
        var probe = install.GetProperty("data").GetProperty("probe");
        Assert.IsTrue(probe.GetProperty("installed").GetBoolean());
        Assert.IsTrue(game.Exists("BepInEx/plugins/SpecTuner/SpecTuner.Probe.dll"));
        var state = GameState.For(game.Root);
        var entry = Ledger.Load(state, state.LoadBaseline()!).Find(probe.GetProperty("changeId").GetString()!)!;
        Assert.AreEqual(ChangeKinds.Probe, entry.Kind);
        CollectionAssert.AreEqual(new[] { "BepInEx/plugins/SpecTuner/SpecTuner.Probe.dll" }, entry.Files);
        Assert.AreEqual(ProbeInstaller.SourceHash, state.LoadRecord()!.Probe!.SourceHash);
        Assert.IsTrue(Run("detect", new { gamePath = game.Root }).GetProperty("data").GetProperty("probe").GetProperty("installed").GetBoolean());
    }

    [TestMethod]
    public void ProbeThatDoesNotCompile_FailsCleanly_AndChangesNothing()
    {
        var game = CreateGame();
        File.Delete(game.Full($"{game.Name}_Data/Managed/UnityEngine.dll")); // nothing to compile against
        var before = game.Hashes();

        var envelope = Run("install", new { gamePath = game.Root });

        AssertRefused(envelope, "PROBE_BUILD_FAILED");
        Assert.IsGreaterThan(0, envelope.GetProperty("data").GetProperty("errorCount").GetInt32());
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
        Assert.IsFalse(game.Exists("BepInEx"));
        var state = GameState.For(game.Root);
        Assert.IsFalse(File.Exists(state.ChangesPath) && Ledger.Load(state, state.LoadBaseline()!).Entries.Count > 0);
    }

    [TestMethod]
    public void EntrypointIsMovedToCamera_OnlyForUnity6()
    {
        Assert.AreEqual("Camera", BepInExConfig.EntrypointFor("6000.6.9f1"));
        Assert.IsNull(BepInExConfig.EntrypointFor("2022.3.10f1"));
        Assert.IsNull(BepInExConfig.EntrypointFor(null));
    }

    [TestMethod]
    public void EnsureEntrypoint_WritesMissingConfig_EditsBepInExsOwn_AndIsIdempotent()
    {
        var root = Path.Combine(TempDir, "cfg");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "BepInEx", "config", "BepInEx.cfg");

        Assert.IsTrue(BepInExConfig.EnsureEntrypoint(root, "Camera"));
        StringAssert.Contains(File.ReadAllText(path), "Type = Camera");
        Assert.IsFalse(BepInExConfig.EnsureEntrypoint(root, "Camera"));

        File.WriteAllText(path, "[Chainloader]\nHideManagerGameObject = false\n\n[Preloader.Entrypoint]\nAssembly = UnityEngine.CoreModule.dll\nType = Application\nMethod = .cctor\n\n[Logging]\nType = Something\n");
        Assert.IsTrue(BepInExConfig.EnsureEntrypoint(root, "Camera"));
        var text = File.ReadAllText(path);
        StringAssert.Contains(text, "[Preloader.Entrypoint]\nAssembly = UnityEngine.CoreModule.dll\nType = Camera\n");
        StringAssert.Contains(text, "[Logging]\nType = Something", "only the entrypoint section changes");
    }
}
