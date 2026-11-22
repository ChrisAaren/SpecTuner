using System.Text.Json;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Quality;
using SpecTuner.Sidecar.Tests.Support;
using SpecTuner.Probe;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class QualityTests : SidecarTest
{
    private const string QualityFile = "BepInEx/config/spectuner.quality.json";

    private static JsonElement Settings(object value) => JsonSerializer.SerializeToElement(value);

    private static SortedDictionary<string, object> Merge(SortedDictionary<string, object> current, object settings, int? levels = null) =>
        QualitySettingsFile.Merge(current, Settings(settings), levels);

    [TestMethod]
    public void UnknownKey_IsRefused_WithTheValidKeys()
    {
        var ex = Assert.ThrowsExactly<SidecarException>(() => Merge([], new { bloom = 1 }));
        Assert.AreEqual(ErrorCodes.InvalidSetting, ex.Code);
        StringAssert.Contains(ex.Detail!, "renderScale");
        StringAssert.Contains(ex.Detail!, "realtimeReflectionProbes");
    }

    [TestMethod]
    [DataRow("{\"renderScale\": 3}")]
    [DataRow("{\"renderScale\": 0.05}")]
    [DataRow("{\"renderScale\": \"half\"}")]
    [DataRow("{\"antiAliasing\": 3}")]
    [DataRow("{\"targetFrameRate\": 5}")]
    [DataRow("{\"vSyncCount\": 1.5}")]
    [DataRow("{\"shadows\": \"Ultra\"}")]
    [DataRow("{\"softParticles\": 1}")]
    [DataRow("{\"shadowResolution\": 1000}")]
    [DataRow("{\"resolutionWidth\": 1280}")]
    public void OutOfRangeOrWrongType_IsRefused(string json)
    {
        using var document = JsonDocument.Parse(json);
        var ex = Assert.ThrowsExactly<SidecarException>(() => QualitySettingsFile.Merge([], document.RootElement, null));
        Assert.AreEqual(ErrorCodes.InvalidSetting, ex.Code, ex.Message);
    }

    [TestMethod]
    public void ValidValues_AreNormalized()
    {
        var merged = Merge([], new { renderScale = 0.75, targetFrameRate = -1, shadows = "hardonly", antiAliasing = 4, softParticles = false, resolutionWidth = 1280, resolutionHeight = 720 });

        Assert.AreEqual(0.75, merged["renderScale"]);
        Assert.AreEqual(-1L, merged["targetFrameRate"]);
        Assert.AreEqual("HardOnly", merged["shadows"]);
        Assert.AreEqual(4L, merged["antiAliasing"]);
        Assert.IsFalse((bool)merged["softParticles"]);
    }

    [TestMethod]
    public void Null_RemovesAKey_AndOtherKeysAreKept()
    {
        var current = Merge([], new { renderScale = 0.75, shadowDistance = 40 });

        var merged = Merge(current, new Dictionary<string, object?> { ["renderScale"] = null, ["lodBias"] = 0.5 });

        CollectionAssert.AreEquivalent(new[] { "lodBias", "shadowDistance" }, merged.Keys.ToList());
    }

    [TestMethod]
    public void QualityLevel_IsLimitedToTheGamesLevels_WhenKnown()
    {
        Merge([], new { qualityLevel = 5 });
        var ex = Assert.ThrowsExactly<SidecarException>(() => Merge([], new { qualityLevel = 5 }, levels: 3));
        StringAssert.Contains(ex.Message, "0 to 2");
    }

    [TestMethod]
    public void SetQuality_RecordsOneChangePerCall_AndRevertPutsTheFileBack()
    {
        var game = CreateGame();
        var first = Run("set-quality", new { gamePath = game.Root, title = "Lower shadows", settings = new { shadowDistance = 40 } });
        AssertOk(first);
        var firstContent = File.ReadAllText(game.Full(QualityFile));
        var second = Run("set-quality", new { gamePath = game.Root, title = "Lower render scale", settings = new { renderScale = 0.8 } });
        AssertOk(second);

        var overrides = second.GetProperty("data").GetProperty("overrides");
        Assert.AreEqual(40, overrides.GetProperty("shadowDistance").GetDouble());
        Assert.AreEqual(0.8, overrides.GetProperty("renderScale").GetDouble());
        var changes = Run("changes", new { gamePath = game.Root }).GetProperty("data").GetProperty("changes");
        Assert.AreEqual(4, changes.GetArrayLength(), "loader, probe and two quality changes");
        Assert.AreEqual("Lower render scale", changes[0].GetProperty("title").GetString());
        Assert.AreEqual("quality", changes[0].GetProperty("kind").GetString());

        // Undo the newer one: the file goes back to the first version.
        AssertOk(Run("revert", new { gamePath = game.Root, changeId = second.GetProperty("data").GetProperty("changeId").GetString() }));
        Assert.AreEqual(firstContent, File.ReadAllText(game.Full(QualityFile)));

        // Undo the first: the file did not exist before it, so it disappears.
        AssertOk(Run("revert", new { gamePath = game.Root, changeId = first.GetProperty("data").GetProperty("changeId").GetString() }));
        Assert.IsFalse(game.Exists(QualityFile));
    }

    [TestMethod]
    public void SetQuality_WithNothingNew_RecordsNoChange()
    {
        var game = CreateGame();
        AssertOk(Run("set-quality", new { gamePath = game.Root, settings = new { lodBias = 0.5 } }));

        var again = Run("set-quality", new { gamePath = game.Root, settings = new { lodBias = 0.5 } });

        AssertOk(again);
        Assert.AreEqual(JsonValueKind.Null, again.GetProperty("data").GetProperty("changeId").ValueKind);
        Assert.AreEqual(3, Run("changes", new { gamePath = game.Root }).GetProperty("data").GetProperty("changes").GetArrayLength());
    }

    [TestMethod]
    public void SetQuality_InvalidSetting_ChangesNothing()
    {
        var game = CreateGame();
        var before = game.Hashes();

        AssertRefused(Run("set-quality", new { gamePath = game.Root, settings = new { renderScale = 9 } }), "INVALID_SETTING");
        AssertRefused(Run("set-quality", new { gamePath = game.Root, settings = new { } }), "BAD_REQUEST");

        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void TheProbesJsonReader_ReadsWhatTheSidecarWrites()
    {
        var game = CreateGame();
        AssertOk(Run("set-quality", new { gamePath = game.Root, settings = new { renderScale = 0.85, shadows = "Disable", softParticles = true, qualityLevel = 2 } }));

        var parsed = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(game.Full(QualityFile)));

        Assert.AreEqual(0.85, parsed["renderScale"]);
        Assert.AreEqual("Disable", parsed["shadows"]);
        Assert.IsTrue((bool)parsed["softParticles"]);
        Assert.AreEqual(2.0, parsed["qualityLevel"]);
    }

    [TestMethod]
    public void BenchmarkRequest_IsNeverSnapshotted()
    {
        var game = CreateGame();
        game.Write(QualityFile, "{}");
        game.Write("BepInEx/config/spectuner.benchmark.json", "{}");

        var files = State.OurFiles.SnapshottedFiles(game.Root).ToList();

        CollectionAssert.AreEqual(new[] { QualityFile }, files);
    }
}
