using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.State;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class LedgerTests : SidecarTest
{
    private const string QualityFile = "BepInEx/config/spectuner.quality.json";

    private GameFixture _game = null!;
    private GameState _state = null!;
    private Baseline _baseline = null!;

    [TestInitialize]
    public void SetUpGame()
    {
        _game = CreateGame();
        _state = GameState.For(_game.Root);
        _state.LoadOrCreateRecord(_game.Name);
        _baseline = Baseline.Capture(_game.Root);
        _state.SaveBaseline(_baseline);
    }

    private Ledger Reload() => Ledger.Load(_state, _state.LoadBaseline()!);

    private string Apply(Ledger ledger, string kind, string title, string relative, string content)
    {
        var change = ledger.Begin(kind, title, "test");
        _game.Write(relative, content);
        ledger.Commit(change, [relative]);
        return change.Id;
    }

    [TestMethod]
    public void BeginCommit_RecordsChange_AndRevertRemovesTheNewFile()
    {
        var before = _game.Hashes();
        var ledger = Ledger.Load(_state, _baseline);
        var id = Apply(ledger, ChangeKinds.Mod, "Double jump", "BepInEx/plugins/SpecTuner/DoubleJump.dll", "mod");

        var entry = Reload().Find(id)!;
        Assert.AreEqual("c0001", id);
        Assert.AreEqual(ChangeStatus.Active, entry.Status);
        Assert.IsFalse(entry.Verified);
        CollectionAssert.AreEqual(new[] { "BepInEx/plugins/SpecTuner/DoubleJump.dll" }, entry.Files);
        Assert.IsTrue(Directory.Exists(Path.Combine(_state.SnapshotsDir, id)));

        var reverted = Reload().Revert(id);

        Assert.AreEqual(ChangeStatus.Reverted, reverted.Status);
        Assert.AreEqual(ChangeStatus.Reverted, Reload().Find(id)!.Status);
        CollectionAssert.AreEqual(before.ToList(), _game.Hashes().ToList());
        Assert.IsFalse(_game.Exists("BepInEx"), "empty folders we created are removed too");
    }

    [TestMethod]
    public void Revert_PutsBackThePreviousContentOfOurFile()
    {
        var ledger = Ledger.Load(_state, _baseline);
        Apply(ledger, ChangeKinds.Quality, "Lower shadows", QualityFile, "{\"shadowDistance\":40}");
        var second = Apply(ledger, ChangeKinds.Quality, "Lower render scale", QualityFile, "{\"shadowDistance\":40,\"renderScale\":0.85}");

        Reload().Revert(second);

        Assert.AreEqual("{\"shadowDistance\":40}", File.ReadAllText(_game.Full(QualityFile)));
    }

    [TestMethod]
    public void Revert_IsBlockedByALaterChangeToTheSameFile()
    {
        var ledger = Ledger.Load(_state, _baseline);
        var first = Apply(ledger, ChangeKinds.Quality, "Lower shadows", QualityFile, "a");
        Apply(ledger, ChangeKinds.Quality, "Lower render scale", QualityFile, "b");

        var ex = Assert.ThrowsExactly<SidecarException>(() => Reload().Revert(first));

        Assert.AreEqual(ErrorCodes.RevertBlocked, ex.Code);
        StringAssert.Contains(ex.Message, "Lower render scale");
        Assert.AreEqual("b", File.ReadAllText(_game.Full(QualityFile)));
    }

    [TestMethod]
    public void Revert_OfAnIndependentOlderChange_LeavesNewerChangesAlone()
    {
        var ledger = Ledger.Load(_state, _baseline);
        var first = Apply(ledger, ChangeKinds.Mod, "Mod A", "BepInEx/plugins/SpecTuner/A.dll", "a");
        Apply(ledger, ChangeKinds.Mod, "Mod B", "BepInEx/plugins/SpecTuner/B.dll", "b");

        Reload().Revert(first);

        Assert.IsFalse(_game.Exists("BepInEx/plugins/SpecTuner/A.dll"));
        Assert.AreEqual("b", File.ReadAllText(_game.Full("BepInEx/plugins/SpecTuner/B.dll")));
    }

    [TestMethod]
    public void Revert_Twice_IsChangeNotActive_AndUnknownIdIsChangeNotFound()
    {
        var ledger = Ledger.Load(_state, _baseline);
        var id = Apply(ledger, ChangeKinds.Mod, "Mod", "BepInEx/plugins/SpecTuner/A.dll", "a");
        ledger.Revert(id);

        Assert.AreEqual(ErrorCodes.ChangeNotActive, Assert.ThrowsExactly<SidecarException>(() => ledger.Revert(id)).Code);
        Assert.AreEqual(ErrorCodes.ChangeNotFound, Assert.ThrowsExactly<SidecarException>(() => ledger.Revert("c0099")).Code);
    }

    [TestMethod]
    public void Probe_CannotBeRevertedAloneWhileOtherChangesDependOnIt()
    {
        var ledger = Ledger.Load(_state, _baseline);
        var probe = Apply(ledger, ChangeKinds.Probe, "Probe", "BepInEx/plugins/SpecTuner/SpecTuner.Probe.dll", "p");
        Apply(ledger, ChangeKinds.Quality, "Lower shadows", QualityFile, "q");

        Assert.AreEqual(ErrorCodes.RevertBlocked, Assert.ThrowsExactly<SidecarException>(() => Reload().Revert(probe)).Code);
    }

    [TestMethod]
    public void RevertAllUnverified_UndoesOnlyChangesSinceTheLastGoodStart()
    {
        var ledger = Ledger.Load(_state, _baseline);
        var good = Apply(ledger, ChangeKinds.Quality, "Lower shadows", QualityFile, "good");
        ledger.MarkAllLiveVerified();
        var bad1 = Apply(ledger, ChangeKinds.Quality, "Lower render scale", QualityFile, "bad");
        var bad2 = Apply(ledger, ChangeKinds.Mod, "Broken mod", "BepInEx/plugins/SpecTuner/Broken.dll", "x");

        ledger = Reload();
        var reverted = GameRestorer.RevertAllUnverified(_state, _state.LoadRecord()!, _baseline, ledger);

        CollectionAssert.AreEqual(new[] { bad2, bad1 }, reverted.ToList());
        Assert.AreEqual("good", File.ReadAllText(_game.Full(QualityFile)));
        Assert.IsFalse(_game.Exists("BepInEx/plugins/SpecTuner/Broken.dll"));
        Assert.AreEqual(ChangeStatus.Active, Reload().Find(good)!.Status);
        Assert.IsTrue(Reload().Find(good)!.Verified);
    }

    [TestMethod]
    public void MarkVerified_IsSaved()
    {
        var ledger = Ledger.Load(_state, _baseline);
        var id = Apply(ledger, ChangeKinds.Mod, "Mod", "BepInEx/plugins/SpecTuner/A.dll", "a");

        ledger.MarkVerified(id);

        Assert.IsTrue(Reload().Find(id)!.Verified);
    }

    [TestMethod]
    public void Abandon_PutsFilesBackAndDropsTheChange()
    {
        var ledger = Ledger.Load(_state, _baseline);
        Apply(ledger, ChangeKinds.Quality, "Lower shadows", QualityFile, "kept");
        var change = ledger.Begin(ChangeKinds.Quality, "Half done", "test");
        _game.Write(QualityFile, "half written");
        _game.Write("BepInEx/plugins/SpecTuner/Partial.dll", "x");

        ledger.Abandon(change, [QualityFile, "BepInEx/plugins/SpecTuner/Partial.dll"]);

        Assert.AreEqual("kept", File.ReadAllText(_game.Full(QualityFile)));
        Assert.IsFalse(_game.Exists("BepInEx/plugins/SpecTuner/Partial.dll"));
        Assert.HasCount(1, Reload().Entries);
        Assert.IsFalse(Directory.Exists(Path.Combine(_state.SnapshotsDir, change.Id)));
    }

    [TestMethod]
    public void Ledger_RefusesToTouchAnOriginalGameFile()
    {
        var original = $"{_game.DataRelative}/level0";
        var before = File.ReadAllBytes(_game.Full(original));
        var ledger = Ledger.Load(_state, _baseline);
        var change = ledger.Begin(ChangeKinds.Mod, "Bad", "test");
        ledger.Commit(change, [original]);

        Assert.ThrowsExactly<InvalidOperationException>(() => ledger.Revert(change.Id));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(_game.Full(original)));
    }

    [TestMethod]
    public void StateFiles_AreWrittenWithoutLeftoverTempFiles()
    {
        var ledger = Ledger.Load(_state, _baseline);
        Apply(ledger, ChangeKinds.Mod, "Mod", "BepInEx/plugins/SpecTuner/A.dll", "a");

        var temps = Directory.GetFiles(_state.Dir, "*.tmp", SearchOption.AllDirectories);
        Assert.IsEmpty(temps);
        Assert.IsTrue(File.Exists(_state.ChangesPath));
    }

    [TestMethod]
    public void Baseline_HasSizeTimeAndHashForEveryFile()
    {
        var hashes = _game.Hashes();

        Assert.AreEqual(hashes.Count, _baseline.FileCount);
        foreach (var file in _baseline.Files)
        {
            Assert.AreEqual(hashes[file.Path], file.Sha256, file.Path);
            Assert.AreEqual(new FileInfo(_game.Full(file.Path)).Length, file.Size);
            Assert.AreEqual(new FileInfo(_game.Full(file.Path)).LastWriteTimeUtc, file.LastWriteUtc);
        }
        var reloaded = _state.LoadBaseline()!;
        Assert.AreEqual(_baseline.Files[0].LastWriteUtc, reloaded.Files[0].LastWriteUtc, "time must survive the JSON round trip exactly");
    }
}
