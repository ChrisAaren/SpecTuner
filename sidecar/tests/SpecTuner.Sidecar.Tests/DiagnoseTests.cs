using System.Text.Json;
using System.Text.RegularExpressions;
using SpecTuner.Sidecar.Platform;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class DiagnoseTests : SidecarTest
{
    private static string Raw(object request) => RunRaw(["diagnose"], Cli.Json.Serialize(request)).Stdout;

    [TestMethod]
    public void WithoutAGame_ReportsTheSystem()
    {
        var envelope = Run("diagnose", new { });

        AssertOk(envelope);
        var data = envelope.GetProperty("data");
        Assert.AreEqual(Platforms.NameOf(Platforms.Host), data.GetProperty("system").GetProperty("os").GetString());
        Assert.AreEqual(Platforms.Current.Name, data.GetProperty("sidecar").GetProperty("platform").GetString());
        Assert.AreEqual("<state>", data.GetProperty("stateFolder").GetProperty("path").GetString());
        Assert.IsFalse(data.TryGetProperty("game", out _));
        StringAssert.StartsWith(data.GetProperty("text").GetString(), "SpecTuner diagnosis");
    }

    [TestMethod]
    public void OutOfScopeFolder_IsReported_AndNothingIsWritten()
    {
        var game = CreateGame();
        game.Write(game.GameAssemblyRelative, PeBuilder.Build());
        var before = game.Hashes();

        var envelope = Run("diagnose", new { gamePath = game.Root });

        AssertOk(envelope);
        var report = envelope.GetProperty("data").GetProperty("game");
        Assert.IsFalse(report.GetProperty("supported").GetBoolean());
        Assert.AreEqual("IL2CPP_NOT_SUPPORTED", report.GetProperty("refusal").GetProperty("code").GetString());
        Assert.IsTrue(report.GetProperty("markers").GetProperty("gameAssembly").GetBoolean());
        StringAssert.Contains(envelope.GetProperty("summary").GetString(), "IL2CPP_NOT_SUPPORTED");
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
        Assert.IsFalse(Directory.Exists(Path.Combine(Home, "games")), "diagnose must not write state");
    }

    [TestMethod]
    public void InstalledGame_ReportsLoaderLogsAndState()
    {
        var game = CreateGame();
        AssertOk(Run("install", new { gamePath = game.Root }));
        game.Write("BepInEx/LogOutput.log", string.Join("\n",
            "[Message:   BepInEx] BepInEx 5.4.23.6 - Fixture Game",
            "[Info   :   BepInEx] Loading [SpecTuner Probe 1.0.0]",
            "[Message:   BepInEx] Chainloader startup complete",
            "[Error  : Unity Log] NullReferenceException at Fixture.Update ()"));
        var stateFiles = GameFixture.Hashes(Home);

        var data = Run("diagnose", new { gamePath = game.Root }).GetProperty("data");

        var loader = data.GetProperty("loader");
        Assert.AreEqual(Platforms.Current.Loader.ResourceName, loader.GetProperty("package").GetString());
        foreach (var file in loader.GetProperty("files").EnumerateArray())
        {
            Assert.IsTrue(file.GetProperty("present").GetBoolean(), file.ToString());
            Assert.IsTrue(file.GetProperty("matchesPackage").GetBoolean(), file.ToString());
        }
        Assert.AreEqual("5.4.23.6", loader.GetProperty("bepinexVersion").GetString());
        Assert.AreEqual("4.6.0", loader.GetProperty("doorstopVersion").GetString());
        var log = data.GetProperty("logs").GetProperty("bepinex");
        Assert.IsTrue(log.GetProperty("chainloaderStarted").GetBoolean());
        Assert.AreEqual("SpecTuner Probe 1.0.0", log.GetProperty("loadedPlugins")[0].GetString());
        Assert.AreEqual(1, log.GetProperty("errorCount").GetInt32());
        var state = data.GetProperty("spectuner");
        Assert.IsTrue(state.GetProperty("hasRecord").GetBoolean());
        Assert.AreEqual(2, state.GetProperty("changes").EnumerateObject().Sum(p => p.Value.GetInt32()));
        Assert.IsFalse(data.GetProperty("gameRunning").GetBoolean());
        Assert.IsGreaterThan(0, data.GetProperty("game").GetProperty("programs").GetArrayLength());
        CollectionAssert.AreEqual(stateFiles.ToList(), GameFixture.Hashes(Home).ToList(), "diagnose writes no state");
    }

    [TestMethod]
    public void Output_LeavesOutHomeFoldersUserAndComputerNames()
    {
        var user = Environment.UserName;
        var game = CreateGame();
        AssertOk(Run("install", new { gamePath = game.Root }));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        game.Write("BepInEx/LogOutput.log", string.Join("\n",
            $"[Error  : Unity Log] Exception in {home}{Path.DirectorySeparatorChar}Games{Path.DirectorySeparatorChar}save.dat",
            $"[Error  : Unity Log] Exception in /home/{user}/Games/save.dat on {Environment.MachineName}",
            $"[Error  : Unity Log] Exception in C:\\Users\\{user}\\Games\\save.dat",
            $"[Error  : Unity Log] Exception in {game.Root}"));

        var raw = Raw(new { gamePath = game.Root });

        using var document = JsonDocument.Parse(raw);
        Assert.IsTrue(document.RootElement.GetProperty("ok").GetBoolean(), raw);
        var unescaped = document.RootElement.GetRawText() + JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("data").GetProperty("text").GetString();
        foreach (var secret in new[] { home, home.Replace('\\', '/'), game.Root, game.Root.Replace('\\', '/'), TempDir, Home })
            Assert.DoesNotContain(secret, unescaped.Replace("\\\\", "\\"), StringComparison.OrdinalIgnoreCase);
        foreach (var name in new[] { user, Environment.MachineName }.Where(n => n.Length >= 3))
            Assert.IsFalse(Regex.IsMatch(unescaped, @"(?<![A-Za-z0-9])" + Regex.Escape(name) + @"(?![A-Za-z0-9])", RegexOptions.IgnoreCase), $"leaks {name}");
        Assert.IsGreaterThan(0, document.RootElement.GetProperty("data").GetProperty("redactions").GetInt32());
    }

    [TestMethod]
    public void WindowsBuildOnLinux_IsExplainedWithItsProgramHeaders()
    {
        UsePlatform(GamePlatform.Linux);
        var windowsBuild = GameFixture.Create(TempDir, GamePlatform.Windows);

        var data = Run("diagnose", new { gamePath = windowsBuild.Root }).GetProperty("data");

        Assert.AreEqual("linux", data.GetProperty("sidecar").GetProperty("platform").GetString());
        Assert.IsTrue(data.GetProperty("sidecar").GetProperty("platformOverridden").GetBoolean());
        var game = data.GetProperty("game");
        Assert.AreEqual("COMPATIBILITY_LAYER_NOT_SUPPORTED", game.GetProperty("refusal").GetProperty("code").GetString());
        var program = game.GetProperty("programs").EnumerateArray().First(p => p.GetProperty("name").GetString() == "Fixture Game.exe");
        Assert.AreEqual("pe", program.GetProperty("format").GetString());
        Assert.AreEqual("x64", program.GetProperty("arches")[0].GetString());
    }

    [TestMethod]
    public void MacApp_ReportsSliceSignatures()
    {
        var game = CreateGame(GamePlatform.MacOS);

        var data = Run("diagnose", new { gamePath = game.Root }).GetProperty("data");

        var program = data.GetProperty("game").GetProperty("programs")[0];
        Assert.AreEqual("Fixture Game.app/Contents/MacOS/Fixture Game", program.GetProperty("name").GetString());
        Assert.AreEqual("mach-o-universal", program.GetProperty("format").GetString());
        Assert.AreEqual(2, program.GetProperty("slices").GetArrayLength());
        Assert.IsFalse(program.GetProperty("slices")[0].GetProperty("hardenedRuntime").GetBoolean());
        StringAssert.Contains(data.GetProperty("loader").GetProperty("steamLaunchOption").GetString(), "<game>/run_bepinex.sh");
    }
}
