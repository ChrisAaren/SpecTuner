using System.Text.Json;
using System.Text.Json.Nodes;
using SpecTuner.Sidecar.State;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

/// <summary>Past changes in <c>changes</c>, and <c>reapply</c>. Launches use SpecTuner.FakeGame (never a real game).</summary>
[TestClass]
public sealed class ReapplyTests : SidecarTest
{
    private const string QualityFile = "BepInEx/config/spectuner.quality.json";
    private const string Dll = "BepInEx/plugins/SpecTuner/TestMod.dll";

    [TestInitialize]
    public void DefaultFakeGame() => FakeGame("result", "load");

    [TestCleanup]
    public void ClearFakeGame()
    {
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", null);
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_MODS", null);
    }

    private static void FakeGame(string mode, string mods)
    {
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", mode);
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_MODS", mods);
    }

    private static JsonElement Changes(GameFixture game)
    {
        var envelope = Run("changes", new { gamePath = game.Root });
        AssertOk(envelope);
        return envelope.GetProperty("data");
    }

    private static List<JsonElement> Past(GameFixture game) => Changes(game).GetProperty("past").EnumerateArray().ToList();

    private static string SetQuality(GameFixture game, string title, object settings)
    {
        var envelope = Run("set-quality", new { gamePath = game.Root, title, description = title + " (test)", settings });
        AssertOk(envelope);
        return envelope.GetProperty("data").GetProperty("changeId").GetString()!;
    }

    private static string BuildMod(GameFixture game, string version = "1.0.0", string title = "Test mod", bool test = false)
    {
        var envelope = Run("build-plugin", new { gamePath = game.Root, name = "TestMod", title, description = "For a test.", source = ModSources.Plugin(version: version), test });
        AssertOk(envelope);
        return envelope.GetProperty("data").GetProperty("changeId").GetString()!;
    }

    private static JsonElement Reapply(GameFixture game, string changeId, bool? test = null) =>
        test is null ? Run("reapply", new { gamePath = game.Root, changeId }) : Run("reapply", new { gamePath = game.Root, changeId, test });

    private static void Undo(GameFixture game, string changeId) => AssertOk(Run("revert", new { gamePath = game.Root, changeId }));

    [TestMethod]
    public void Undo_KeepsTheChangeAsAPastChange_AndTheExistingFieldsStay()
    {
        var game = CreateGame();
        var id = SetQuality(game, "Lower shadows", new { shadowDistance = 40 });

        Undo(game, id);

        var data = Changes(game);
        // The existing fields, unchanged.
        Assert.AreEqual(3, data.GetProperty("total").GetInt32(), "loader, probe, quality");
        Assert.IsFalse(data.GetProperty("truncated").GetBoolean());
        Assert.IsTrue(data.GetProperty("canRestore").GetBoolean());
        var newest = data.GetProperty("changes")[0];
        Assert.AreEqual(id, newest.GetProperty("id").GetString());
        Assert.AreEqual("reverted", newest.GetProperty("status").GetString());
        Assert.AreEqual("undone", newest.GetProperty("endReason").GetString());

        var past = data.GetProperty("past").EnumerateArray().ToList();
        Assert.HasCount(1, past, "only player-facing kinds; the loader and helper are still in place anyway");
        var item = past[0];
        Assert.AreEqual(id, item.GetProperty("id").GetString());
        Assert.AreEqual("quality", item.GetProperty("kind").GetString());
        Assert.AreEqual("Lower shadows", item.GetProperty("title").GetString());
        Assert.AreEqual("Lower shadows (test)", item.GetProperty("description").GetString());
        Assert.AreEqual("undone", item.GetProperty("reason").GetString());
        Assert.IsTrue(item.GetProperty("canReapply").GetBoolean());
        Assert.IsFalse(item.TryGetProperty("whyNot", out _));
        Assert.IsGreaterThanOrEqualTo(item.GetProperty("createdAt").GetDateTime(), item.GetProperty("endedAt").GetDateTime());
        Assert.AreEqual(1, data.GetProperty("pastTotal").GetInt32());
        Assert.IsFalse(data.GetProperty("pastTruncated").GetBoolean());
    }

    [TestMethod]
    public void RestoreAll_KeepsHistory_OutsideTheGameFolder()
    {
        var game = CreateGame();
        var original = game.Hashes();
        var quality = SetQuality(game, "Lower shadows", new { shadowDistance = 40 });
        var mod = BuildMod(game);

        AssertOk(Run("revert", new { gamePath = game.Root, all = true }));

        CollectionAssert.AreEqual(original.ToList(), game.Hashes().ToList(), "the game folder is exactly as found");
        var data = Changes(game);
        Assert.AreEqual(0, data.GetProperty("changes").GetArrayLength());
        Assert.IsFalse(data.GetProperty("canRestore").GetBoolean());
        var past = data.GetProperty("past").EnumerateArray().ToList();
        CollectionAssert.AreEqual(new[] { mod, quality }, past.Select(p => p.GetProperty("id").GetString()).ToArray(), "newest first");
        Assert.IsTrue(past.All(p => p.GetProperty("reason").GetString() == "restored" && p.GetProperty("canReapply").GetBoolean()));
        Assert.AreEqual("TestMod", past[0].GetProperty("mod").GetProperty("name").GetString());
        Assert.AreEqual(1, past[0].GetProperty("mod").GetProperty("build").GetInt32());
    }

    [TestMethod]
    public void FailedTest_AndFailedStart_AreHistory_ThatCannotBeReapplied()
    {
        var game = CreateGame().UseFakeGameExe();
        FakeGame("result", "none");
        AssertRefused(Run("build-plugin", new { gamePath = game.Root, name = "TestMod", title = "Test mod", source = ModSources.Plugin() }), "MOD_FAILED_TEST");
        var quality = SetQuality(game, "Lower shadows", new { shadowDistance = 40 });
        FakeGame("crash", "load");
        AssertRefused(Run("launch-and-benchmark", new { gamePath = game.Root, warmupSeconds = 0, measureSeconds = 1, startupTimeoutSeconds = 30 }), "GAME_FAILED_TO_START");

        var past = Past(game);

        Assert.HasCount(2, past);
        Assert.AreEqual(quality, past[0].GetProperty("id").GetString());
        Assert.AreEqual("failed_start", past[0].GetProperty("reason").GetString());
        Assert.AreEqual("failed_test", past[1].GetProperty("reason").GetString());
        foreach (var item in past)
        {
            Assert.IsFalse(item.GetProperty("canReapply").GetBoolean());
            Assert.IsFalse(string.IsNullOrWhiteSpace(item.GetProperty("whyNot").GetString()));
        }

        var refused = Reapply(game, past[1].GetProperty("id").GetString()!);
        AssertRefused(refused, "CANNOT_REAPPLY");
        Assert.AreEqual(past[1].GetProperty("whyNot").GetString(), refused.GetProperty("error").GetProperty("message").GetString());
    }

    [TestMethod]
    public void Reapply_Quality_PutsTheSettingsBack_AsANewChange_ThatCanBeUndoneAgain()
    {
        var game = CreateGame();
        var id = SetQuality(game, "Lower shadows", new { shadowDistance = 40, renderScale = 0.8 });
        var applied = File.ReadAllText(game.Full(QualityFile));
        Undo(game, id);
        Assert.IsFalse(game.Exists(QualityFile));

        var envelope = Reapply(game, id);

        AssertOk(envelope);
        var data = envelope.GetProperty("data");
        var newId = data.GetProperty("changeId").GetString()!;
        Assert.AreNotEqual(id, newId);
        Assert.AreEqual(id, data.GetProperty("reappliedFrom").GetString());
        Assert.AreEqual(40, data.GetProperty("overrides").GetProperty("shadowDistance").GetInt32());
        StringAssert.Contains(envelope.GetProperty("summary").GetString()!, "Lower shadows");
        Assert.AreEqual(applied, File.ReadAllText(game.Full(QualityFile)), "the same settings are in effect");
        var newest = Changes(game).GetProperty("changes")[0];
        Assert.AreEqual("Lower shadows", newest.GetProperty("title").GetString());
        Assert.AreEqual("Lower shadows (test)", newest.GetProperty("description").GetString());
        Assert.IsEmpty(Past(game), "the original is in place again through its copy, so it is not offered");
        AssertRefused(Reapply(game, id), "CHANGE_STILL_ACTIVE");

        Undo(game, newId);

        Assert.IsFalse(game.Exists(QualityFile), "undoing the new change behaves like undoing a fresh set-quality");
        var past = Past(game);
        Assert.HasCount(1, past, "the newest copy stands for the original");
        Assert.AreEqual(newId, past[0].GetProperty("id").GetString());
        Assert.AreEqual(id, past[0].GetProperty("reappliedFrom").GetString());
    }

    [TestMethod]
    public void Reapply_Mod_RebuildsFromTheSavedSource_AndTestsIt()
    {
        var game = CreateGame().UseFakeGameExe();
        var id = BuildMod(game, test: true);
        Undo(game, id);
        Assert.IsFalse(game.Exists(Dll));

        var envelope = Reapply(game, id);

        AssertOk(envelope);
        var data = envelope.GetProperty("data");
        Assert.AreEqual(id, data.GetProperty("reappliedFrom").GetString());
        Assert.AreNotEqual(id, data.GetProperty("changeId").GetString());
        Assert.IsFalse(data.GetProperty("replaces").GetBoolean());
        var test = data.GetProperty("test");
        Assert.IsTrue(test.GetProperty("ran").GetBoolean());
        Assert.IsTrue(test.GetProperty("loaded").GetBoolean());
        Assert.AreEqual("[Info   :  Test Mod] loaded", test.GetProperty("logLines")[0].GetString());
        Assert.IsTrue(game.Exists(Dll));
        var state = GameState.For(game.Root);
        Assert.IsTrue(File.Exists(Path.Combine(state.Dir, "mods", "TestMod", data.GetProperty("changeId").GetString()!, "Plugin.cs")), "the rebuild keeps its source too");
    }

    [TestMethod]
    public void Reapply_ReplacedBuild_ReplacesTheCurrentOne_AndBuildsAreKeptApart()
    {
        var game = CreateGame();
        var first = BuildMod(game, "1.0.0", "Test mod v1");
        var firstDll = File.ReadAllBytes(game.Full(Dll));
        var second = BuildMod(game, "1.0.1", "Test mod v2");

        var past = Past(game);
        Assert.HasCount(1, past);
        Assert.AreEqual(first, past[0].GetProperty("id").GetString());
        Assert.AreEqual("replaced", past[0].GetProperty("reason").GetString());
        Assert.AreEqual(1, past[0].GetProperty("mod").GetProperty("build").GetInt32());

        // The current list names the build of every mod change too, so the UI can label "version 2" next to a past "version 1".
        var mods = Run("changes", new { gamePath = game.Root }).GetProperty("data").GetProperty("mods");
        Assert.AreEqual(2, mods.GetProperty(second).GetProperty("build").GetInt32());
        Assert.AreEqual(1, mods.GetProperty(first).GetProperty("build").GetInt32());
        Assert.AreEqual(mods.GetProperty(first).GetProperty("name").GetString(), mods.GetProperty(second).GetProperty("name").GetString());

        var envelope = Reapply(game, first, test: false);

        AssertOk(envelope);
        Assert.IsTrue(envelope.GetProperty("data").GetProperty("replaces").GetBoolean());
        StringAssert.Contains(envelope.GetProperty("summary").GetString()!, "replaces the previous version");
        Assert.AreEqual("ran", envelope.GetProperty("data").GetProperty("test").EnumerateObject().Single().Name);
        CollectionAssert.AreEqual(firstDll, File.ReadAllBytes(game.Full(Dll)), "the same source, built again");
        past = Past(game);
        Assert.HasCount(1, past, "v1 is in place again through its copy; v2 is now the replaced build");
        Assert.AreEqual(second, past[0].GetProperty("id").GetString());
        Assert.AreEqual(2, past[0].GetProperty("mod").GetProperty("build").GetInt32());
    }

    [TestMethod]
    public void Reapply_AfterRestoreAll_InstallsTheLoaderAgain_AndNeverReusesIds()
    {
        var game = CreateGame().UseFakeGameExe();
        var original = game.Hashes();
        var quality = SetQuality(game, "Lower shadows", new { shadowDistance = 40 });
        var mod = BuildMod(game, test: true);
        AssertOk(Run("revert", new { gamePath = game.Root, all = true }));
        CollectionAssert.AreEqual(original.ToList(), game.Hashes().ToList());

        var modAgain = Reapply(game, mod);
        var qualityAgain = Reapply(game, quality);

        AssertOk(modAgain);
        AssertOk(qualityAgain);
        Assert.IsTrue(modAgain.GetProperty("data").GetProperty("test").GetProperty("loaded").GetBoolean());
        Assert.IsTrue(game.Exists("BepInEx/core/BepInEx.dll"));
        Assert.IsTrue(game.Exists(Dll));
        Assert.IsTrue(game.Exists(QualityFile));
        var changes = Changes(game).GetProperty("changes").EnumerateArray().ToList();
        CollectionAssert.IsSubsetOf(new[] { "loader", "probe", "mod", "quality" }, changes.Select(c => c.GetProperty("kind").GetString()).ToArray());
        var oldIds = new[] { quality, mod };
        Assert.IsTrue(changes.All(c => !oldIds.Contains(c.GetProperty("id").GetString())), "ids continue after a restore");
        Assert.IsEmpty(Past(game));

        // And it can all be removed again.
        AssertOk(Run("revert", new { gamePath = game.Root, all = true }));
        CollectionAssert.AreEqual(original.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void Refusals()
    {
        var game = CreateGame();
        var active = SetQuality(game, "Lower shadows", new { shadowDistance = 40 });
        var loader = Changes(game).GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("kind").GetString() == "loader").GetProperty("id").GetString()!;

        AssertRefused(Reapply(game, "c0999"), "CHANGE_NOT_FOUND");
        AssertRefused(Run("reapply", new { gamePath = game.Root }), "BAD_REQUEST");
        AssertRefused(Reapply(game, active), "CHANGE_STILL_ACTIVE");
        AssertRefused(Reapply(game, loader), "CANNOT_REAPPLY");

        // A quality change recorded before settings were kept.
        Undo(game, active);
        var state = GameState.For(game.Root);
        var ledger = JsonNode.Parse(File.ReadAllText(state.ChangesPath))!;
        foreach (var change in ledger["changes"]!.AsArray())
            change!.AsObject().Remove("settings");
        File.WriteAllText(state.ChangesPath, ledger.ToJsonString());

        var item = Past(game).Single();
        Assert.IsFalse(item.GetProperty("canReapply").GetBoolean());
        StringAssert.Contains(item.GetProperty("whyNot").GetString()!, "didn't record its settings");
        AssertRefused(Reapply(game, active), "CANNOT_REAPPLY");
    }
}
