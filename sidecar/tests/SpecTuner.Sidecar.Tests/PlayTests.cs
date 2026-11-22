using System.Diagnostics;
using System.Text.Json;
using SpecTuner.Sidecar.Platform;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

/// <summary>"play" against SpecTuner.FakeGame on the host's own platform (Windows here, Linux and macOS in CI).</summary>
[TestClass]
public sealed class PlayTests : SidecarTest
{
    private string _launchFile = "";

    [TestInitialize]
    public void RecordLaunches()
    {
        _launchFile = Path.Combine(TempDir, "launch.json");
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_LAUNCH_FILE", _launchFile);
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", "result");
    }

    [TestCleanup]
    public void ClearFakeGame() => Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", null);

    /// <summary>What the fake game received: its arguments and the loader's environment variables.</summary>
    private JsonElement Launch()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(_launchFile) && DateTime.UtcNow < deadline)
            Thread.Sleep(100);
        Assert.IsTrue(File.Exists(_launchFile), "the fake game did not start");
        for (var i = 0; ; i++)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_launchFile));
                return document.RootElement.Clone();
            }
            catch (IOException) when (i < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static string? Variable(JsonElement launch, string name) =>
        launch.GetProperty("environment").TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> Args(JsonElement launch) => launch.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList();

    private static void AssertLoaderEnvironment(JsonElement launch, bool expected)
    {
        var enabled = Variable(launch, "DOORSTOP_ENABLED");
        switch (Platforms.Host)
        {
            case GamePlatform.Windows:
                // winhttp.dll loads itself; switching it off is Doorstop's documented argument.
                CollectionAssert.AreEqual(expected ? new List<string>() : new List<string> { "--doorstop-enabled", "false" }, Args(launch));
                break;
            case GamePlatform.Linux:
                Assert.AreEqual(expected ? "1" : null, enabled);
                Assert.AreEqual(expected, (Variable(launch, "LD_PRELOAD") ?? "").Split(':').Contains("libdoorstop.so"));
                if (expected)
                    StringAssert.EndsWith(Variable(launch, "DOORSTOP_TARGET_ASSEMBLY"), "/BepInEx/core/BepInEx.Preloader.dll");
                break;
            default:
                Assert.AreEqual(expected ? "1" : null, enabled);
                Assert.AreEqual(expected ? MacLoaderKit.ArchPreference : null, Variable(launch, "ARCHPREFERENCE"));
                break;
        }
    }

    [TestMethod]
    public void WithChanges_StartsTheGameWithTheLoader_AndChangesNothing()
    {
        var game = CreateGame().UseFakeGameExe();
        AssertOk(Run("install", new { gamePath = game.Root }));
        var before = game.Hashes();

        var play = Run("play", new { gamePath = game.Root, withChanges = true });

        AssertOk(play);
        Assert.AreEqual("Started Fixture Game with your changes.", play.GetProperty("summary").GetString());
        var data = play.GetProperty("data");
        Assert.IsTrue(data.GetProperty("started").GetBoolean());
        Assert.IsTrue(data.GetProperty("withChanges").GetBoolean());
        Assert.IsTrue(data.GetProperty("loaderActive").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, data.GetProperty("steamLaunchOption").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, data.GetProperty("note").ValueKind);
        AssertLoaderEnvironment(Launch(), expected: true);
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList(), "play changes no file");
    }

    [TestMethod]
    public void WithoutChanges_SwitchesTheLoaderOffForThisRunOnly()
    {
        var game = CreateGame().UseFakeGameExe();
        AssertOk(Run("install", new { gamePath = game.Root }));
        var before = game.Hashes();

        var play = Run("play", new { gamePath = game.Root, withChanges = false });

        AssertOk(play);
        Assert.AreEqual("Started Fixture Game without SpecTuner's changes.", play.GetProperty("summary").GetString());
        Assert.IsFalse(play.GetProperty("data").GetProperty("loaderActive").GetBoolean());
        Assert.IsFalse(play.GetProperty("data").GetProperty("withChanges").GetBoolean());
        AssertLoaderEnvironment(Launch(), expected: false);
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList(), "play changes no file");
    }

    [TestMethod]
    public void WithChangesByDefault_BeforeAnyInstall_IsAPlainStart()
    {
        var game = CreateGame().UseFakeGameExe();
        var before = game.Hashes();

        var play = Run("play", new { gamePath = game.Root });

        AssertOk(play);
        var data = play.GetProperty("data");
        Assert.IsTrue(data.GetProperty("withChanges").GetBoolean());
        Assert.IsFalse(data.GetProperty("loaderActive").GetBoolean());
        StringAssert.Contains(data.GetProperty("note").GetString(), "hasn't added anything");
        Assert.AreEqual("Started Fixture Game.", play.GetProperty("summary").GetString(), "no changes are claimed when there are none");
        var launch = Launch();
        CollectionAssert.AreEqual(new List<string>(), Args(launch));
        Assert.IsNull(Variable(launch, "DOORSTOP_ENABLED"));
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void SteamGame_GetsTheLaunchOptionOnLinuxAndMacOS()
    {
        var parent = Path.Combine(TempDir, "steamapps", "common");
        Directory.CreateDirectory(parent);
        var game = GameFixture.CreateMono(parent).UseFakeGameExe();
        AssertOk(Run("install", new { gamePath = game.Root }));

        var data = Run("play", new { gamePath = game.Root }).GetProperty("data");

        var option = data.GetProperty("steamLaunchOption");
        switch (Platforms.Host)
        {
            case GamePlatform.Windows:
                Assert.AreEqual(JsonValueKind.Null, option.ValueKind, "winhttp.dll also loads when Steam starts the game");
                Assert.AreEqual(JsonValueKind.Null, data.GetProperty("note").ValueKind);
                break;
            case GamePlatform.Linux:
                Assert.AreEqual("./run_bepinex.sh %command%", option.GetString());
                StringAssert.Contains(data.GetProperty("note").GetString(), "Steam");
                break;
            default:
                Assert.AreEqual($"\"{game.Root}/run_bepinex.sh\" %command%", option.GetString());
                StringAssert.Contains(data.GetProperty("note").GetString(), "Steam");
                break;
        }
        Launch();
    }

    [TestMethod]
    public void TheRealSidecar_ReturnsAtOnce_AndTheGameKeepsRunningOnItsOwn()
    {
        var game = CreateGame().UseFakeGameExe();
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", "linger"); // writes to its output for 8 s
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "spectuner-sidecar.dll"));
        start.ArgumentList.Add("play");
        var clock = Stopwatch.StartNew();
        using var sidecar = Process.Start(start)!;
        sidecar.StandardInput.Write(JsonSerializer.Serialize(new { gamePath = game.Root }));
        sidecar.StandardInput.Close();

        // Reading to the end only finishes once every writer of the pipe is gone: the game must hold none of it.
        var stdout = sidecar.StandardOutput.ReadToEndAsync();
        Assert.IsTrue(stdout.Wait(TimeSpan.FromSeconds(6)), "the sidecar's output stayed open: the game holds the pipe");
        sidecar.WaitForExit();
        Assert.IsLessThan(6.0, clock.Elapsed.TotalSeconds);
        using (var document = JsonDocument.Parse(stdout.Result))
            Assert.IsTrue(document.RootElement.GetProperty("ok").GetBoolean(), stdout.Result);
        Assert.IsTrue(SpecTuner.Sidecar.Games.GameProcesses.IsRunning(game.ExePath), "the game keeps running after the sidecar exits");

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (SpecTuner.Sidecar.Games.GameProcesses.IsRunning(game.ExePath) && DateTime.UtcNow < deadline)
            Thread.Sleep(200);
    }

    [TestMethod]
    public void BadWithChanges_IsBadRequest_AndOutOfScopeGamesAreRefused()
    {
        var game = CreateGame();
        AssertRefused(Run("play", new { gamePath = game.Root, withChanges = "yes" }), "BAD_REQUEST");
        game.Write(game.GameAssemblyRelative, PeBuilder.Build());
        AssertRefused(Run("play", new { gamePath = game.Root }), "IL2CPP_NOT_SUPPORTED");
        AssertRefused(Run("play", new { gamePath = Path.Combine(TempDir, "nope") }), "GAME_NOT_FOUND");
    }

    [TestMethod]
    public void ProgramThatCannotStart_IsGameFailedToStart()
    {
        var game = CreateGame();
        // Linux/macOS: the fixture's program is a bare header without the execute bit. Windows would run a
        // bare x64 header, so it gets the "native" subsystem (a driver), which CreateProcess refuses.
        if (Platforms.Host == GamePlatform.Windows)
        {
            var bytes = PeBuilder.Build();
            bytes[0x40 + 4 + 20 + 68] = 1; // IMAGE_SUBSYSTEM_NATIVE
            game.Write(game.ExeRelative, bytes);
        }
        var play = Run("play", new { gamePath = game.Root });

        AssertRefused(play, "GAME_FAILED_TO_START");
        Assert.IsFalse(play.GetProperty("data").GetProperty("started").GetBoolean());
        StringAssert.Contains(play.GetProperty("error").GetProperty("message").GetString(), "couldn't be started");
    }
}
