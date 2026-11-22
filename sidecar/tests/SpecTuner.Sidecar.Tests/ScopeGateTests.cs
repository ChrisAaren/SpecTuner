using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class ScopeGateTests : SidecarTest
{
    private const string WindowsLayoutOnly = "Windows game layout (.exe, PE header); the Linux and macOS versions are in LinuxScopeTests and MacScopeTests, which run on every system.";

    [TestMethod]
    public void MonoLayout_IsSupported_WithGameFacts()
    {
        var game = CreateGame();

        var envelope = Run("detect", new { gamePath = game.Root });

        AssertOk(envelope);
        var data = envelope.GetProperty("data");
        Assert.IsTrue(data.GetProperty("supported").GetBoolean());
        var facts = data.GetProperty("game");
        Assert.AreEqual("Fixture Game", facts.GetProperty("name").GetString());
        Assert.AreEqual(game.AppDir is null ? Path.GetFileName(game.ExePath) : "Fixture Game.app", facts.GetProperty("exe").GetString());
        Assert.AreEqual("mono", facts.GetProperty("backend").GetString());
        Assert.AreEqual(game.Platform == SpecTuner.Sidecar.Platform.GamePlatform.MacOS ? "arm64+x64" : "x64", facts.GetProperty("arch").GetString());
        Assert.AreEqual("TestCompany", facts.GetProperty("company").GetString());
        Assert.IsFalse(data.GetProperty("loader").GetProperty("installed").GetBoolean());
        Assert.AreEqual(0, data.GetProperty("activeChanges").GetInt32());
    }

    [TestMethod]
    public void SignInCloudSaveAndSteamworks_AreSupported()
    {
        var game = CreateGame();
        foreach (var dll in new[]
                 {
                     "Unity.Services.Authentication.dll", "Unity.Services.Authentication.PlayerAccounts.dll",
                     "Unity.Services.CloudSave.dll", "Unity.Services.Core.dll", "Unity.Services.Core.Networking.dll",
                     "Unity.Services.Core.Analytics.dll", "com.rlabrecque.steamworks.net.dll", "Steamworks.NET.dll",
                 })
            game.Write($"{game.DataRelative}/Managed/{dll}", GameFixture.ManagedAssembly());

        AssertOk(Run("detect", new { gamePath = game.Root }));
    }

    [TestMethod]
    public void Detect_ChangesNothing()
    {
        var game = CreateGame();
        var before = game.Hashes();

        Run("detect", new { gamePath = game.Root });

        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
        Assert.IsFalse(Directory.Exists(Path.Combine(Home, "games")), "detect must not write state");
    }

    [TestMethod]
    public void MissingFolder_IsGameNotFound()
    {
        AssertRefused(Run("detect", new { gamePath = Path.Combine(TempDir, "nope") }), "GAME_NOT_FOUND");
    }

    [TestMethod]
    public void FolderWithoutExe_IsGameNotFound()
    {
        var dir = Path.Combine(TempDir, "empty");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "hi");

        AssertRefused(Run("detect", new { gamePath = dir }), "GAME_NOT_FOUND");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = WindowsLayoutOnly)]
    public void ExeWithoutDataFolder_IsNotUnity()
    {
        var dir = Path.Combine(TempDir, "other");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Game.exe"), PeBuilder.Build());
        File.WriteAllBytes(Path.Combine(dir, "UnityCrashHandler64.exe"), PeBuilder.Build());

        AssertRefused(Run("detect", new { gamePath = dir }), "NOT_UNITY");
    }

    [TestMethod]
    public void DataFolderWithoutUnityFiles_IsNotUnity()
    {
        var game = CreateGame();
        File.Delete(game.Full(game.UnityPlayerRelative));
        File.Delete(game.Full($"{game.DataRelative}/globalgamemanagers"));

        AssertRefused(Run("detect", new { gamePath = game.Root }), "NOT_UNITY");
    }

    [TestMethod]
    [DataRow("GameAssembly.dll", false)]
    [DataRow("Fixture Game_Data/il2cpp_data", true)]
    public void Il2CppLayout_IsDeclined_WithPlainMessage(string marker, bool isDirectory)
    {
        var game = CreateGame();
        marker = game.Native(marker);
        Directory.Delete(game.ManagedDir, recursive: true);
        if (isDirectory)
            game.Write(marker + "/Metadata/global-metadata.dat", GameFixture.RandomBytes(64, 3));
        else
            game.Write(marker, PeBuilder.Build());
        var before = game.Hashes();

        var envelope = Run("detect", new { gamePath = game.Root });

        AssertRefused(envelope, "IL2CPP_NOT_SUPPORTED");
        var message = envelope.GetProperty("error").GetProperty("message").GetString()!;
        StringAssert.Contains(message, "IL2CPP");
        StringAssert.Contains(message, "does not support that kind of game yet");
        StringAssert.Contains(message, "Nothing was changed");
        var data = envelope.GetProperty("data");
        Assert.IsFalse(data.GetProperty("supported").GetBoolean());
        Assert.AreEqual("il2cpp", data.GetProperty("game").GetProperty("backend").GetString());
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = WindowsLayoutOnly)]
    public void X86Exe_IsNotWindowsX64()
    {
        var game = CreateGame();
        game.Write(game.Name + ".exe", PeBuilder.Build(PeBuilder.MachineX86));

        var envelope = Run("detect", new { gamePath = game.Root });

        AssertRefused(envelope, "NOT_WINDOWS_X64");
        Assert.AreEqual("x86", envelope.GetProperty("data").GetProperty("game").GetProperty("arch").GetString());
    }

    [TestMethod]
    [DataRow("EasyAntiCheat", true)]
    [DataRow("EasyAntiCheat_x64.dll", false)]
    [DataRow("start_protected_game.exe", false)]
    [DataRow("BattlEye", true)]
    [DataRow("Fixture Game_Data/Plugins/x86_64/BEClient_x64.dll", false)]
    [DataRow("BEService_x64.exe", false)]
    [DataRow("GameGuard", true)]
    [DataRow("GameGuard.des", false)]
    [DataRow("xigncode", true)]
    [DataRow("x3.xem", false)]
    [DataRow("vgk.sys", false)]
    [DataRow("Fixture Game_Data/Managed/CodeStage.AntiCheat.Runtime.dll", false)]
    [DataRow("Fixture Game_Data/Plugins/x86_64/anticheat_client.dll", false)]
    public void AntiCheatFiles_AreDeclined(string relative, bool isDirectory)
    {
        var game = CreateGame();
        relative = game.Native(relative);
        if (isDirectory)
            game.Mkdir(relative);
        else
            game.Write(relative, PeBuilder.Build());

        var envelope = Run("detect", new { gamePath = game.Root });

        AssertRefused(envelope, "ANTI_CHEAT");
        StringAssert.Contains(envelope.GetProperty("error").GetProperty("message").GetString()!, "anti-cheat");
    }

    [TestMethod]
    public void EncryptedAssemblyCSharp_IsProtectedCode()
    {
        var game = CreateGame();
        var bytes = GameFixture.RandomBytes(8192, 4);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        game.Write($"{game.DataRelative}/Managed/Assembly-CSharp.dll", bytes);

        AssertRefused(Run("detect", new { gamePath = game.Root }), "PROTECTED_CODE");
    }

    [TestMethod]
    public void NativeAssemblyCSharpWithoutMetadata_IsProtectedCode()
    {
        var game = CreateGame();
        game.Write($"{game.DataRelative}/Managed/Assembly-CSharp.dll", PeBuilder.Build());

        AssertRefused(Run("detect", new { gamePath = game.Root }), "PROTECTED_CODE");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = WindowsLayoutOnly)]
    public void SteamDrmBindSection_IsProtectedCode()
    {
        var game = CreateGame();
        game.Write(game.Name + ".exe", PeBuilder.Build(PeBuilder.MachineX64, ".text", ".rdata", ".bind"));

        AssertRefused(Run("detect", new { gamePath = game.Root }), "PROTECTED_CODE");
    }

    [TestMethod]
    [DataRow("Unity.Netcode.Runtime.dll")]
    [DataRow("Unity.Networking.Transport.dll")]
    [DataRow("Unity.Services.Multiplayer.dll")]
    [DataRow("Unity.Services.Relay.dll")]
    [DataRow("Unity.Services.Lobbies.dll")]
    [DataRow("Unity.Services.Matchmaker.dll")]
    [DataRow("Mirror.dll")]
    [DataRow("Mirror.Components.dll")]
    [DataRow("PhotonUnityNetworking.dll")]
    [DataRow("PhotonRealtime.dll")]
    [DataRow("Photon3Unity3D.dll")]
    [DataRow("Fusion.Runtime.dll")]
    [DataRow("FishNet.Runtime.dll")]
    [DataRow("LiteNetLib.dll")]
    [DataRow("DarkRift.Client.dll")]
    [DataRow("RiptideNetworking.dll")]
    [DataRow("Nakama.dll")]
    [DataRow("Unity.Multiplayer.Tools.NetStats.dll")]
    public void NetworkingAssemblies_AreOnlineMultiplayer(string dll)
    {
        var game = CreateGame();
        game.Write($"{game.DataRelative}/Managed/{dll}", GameFixture.ManagedAssembly());

        var envelope = Run("detect", new { gamePath = game.Root });

        AssertRefused(envelope, "ONLINE_MULTIPLAYER");
        StringAssert.Contains(envelope.GetProperty("error").GetProperty("message").GetString()!, "single-player");
    }

    [TestMethod]
    public void OtherCommands_RunTheScopeGate()
    {
        var game = CreateGame();
        game.Write(game.GameAssemblyRelative, PeBuilder.Build());

        AssertRefused(Run("install", new { gamePath = game.Root }), "IL2CPP_NOT_SUPPORTED");
        AssertRefused(Run("changes", new { gamePath = game.Root }), "IL2CPP_NOT_SUPPORTED");
        AssertRefused(Run("revert", new { gamePath = game.Root, all = true }), "IL2CPP_NOT_SUPPORTED");
        Assert.IsFalse(game.Exists("BepInEx"));
    }
}
