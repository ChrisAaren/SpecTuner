using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class CommandHostTests : SidecarTest
{
    [TestMethod]
    public void Success_EnvelopeHasOkCommandSummaryData_AndNoError()
    {
        var game = CreateGame();

        var (envelope, exitCode, stdout) = RunRaw(["detect"], $"{{\"gamePath\": {JsonSerializer.Serialize(game.Root)}}}");

        Assert.AreEqual(0, exitCode);
        var names = envelope.EnumerateObject().Select(p => p.Name).ToList();
        CollectionAssert.AreEqual(new[] { "ok", "command", "summary", "data" }, names);
        Assert.IsTrue(envelope.GetProperty("ok").GetBoolean());
        Assert.AreEqual("detect", envelope.GetProperty("command").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(envelope.GetProperty("summary").GetString()));
        Assert.AreEqual(JsonValueKind.Object, envelope.GetProperty("data").ValueKind);
        Assert.AreEqual(1, stdout.Trim().Split('\n').Count(l => l.StartsWith('{')), "exactly one JSON object");
    }

    [TestMethod]
    public void Refusal_EnvelopeHasErrorCodeMessage_AndNoSummary()
    {
        var (envelope, exitCode, _) = RunRaw(["detect"], "{\"gamePath\": \"Z:/does/not/exist\"}");

        Assert.AreEqual(0, exitCode);
        var names = envelope.EnumerateObject().Select(p => p.Name).ToList();
        CollectionAssert.AreEqual(new[] { "ok", "command", "error", "data" }, names);
        var error = envelope.GetProperty("error");
        Assert.AreEqual("GAME_NOT_FOUND", error.GetProperty("code").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        Assert.IsFalse(envelope.GetProperty("data").GetProperty("supported").GetBoolean());
    }

    [TestMethod]
    public void UnknownCommand_IsRefused()
    {
        var (envelope, exitCode, _) = RunRaw(["frobnicate"], "{}");
        Assert.AreEqual(0, exitCode);
        AssertRefused(envelope, "UNKNOWN_COMMAND");
        Assert.AreEqual("frobnicate", envelope.GetProperty("command").GetString());
    }

    [TestMethod]
    public void NoCommand_IsRefused()
    {
        AssertRefused(RunRaw([], "").Envelope, "UNKNOWN_COMMAND");
    }

    [TestMethod]
    public void BadJson_IsInvalidJson()
    {
        AssertRefused(RunRaw(["detect"], "{ not json").Envelope, "INVALID_JSON");
    }

    [TestMethod]
    [DataRow("[1,2]")]
    [DataRow("{}")]
    [DataRow("{\"gamePath\": 5}")]
    public void WrongShape_IsBadRequest(string request)
    {
        AssertRefused(RunRaw(["detect"], request).Envelope, "BAD_REQUEST");
    }

    [TestMethod]
    [DataRow("search-code")]
    [DataRow("read-code")]
    [DataRow("build-plugin")]
    public void CodeCommands_AreRegistered_AndValidateTheirRequest(string command)
    {
        // Milestone 4 implemented these; an empty request is now a BAD_REQUEST, not NOT_IMPLEMENTED.
        var envelope = RunRaw([command], "{}").Envelope;
        AssertRefused(envelope, "BAD_REQUEST");
        Assert.AreEqual(command, envelope.GetProperty("command").GetString());
    }

    [TestMethod]
    public void RequestFileOption_IsRead()
    {
        var game = CreateGame();
        var requestFile = Path.Combine(TempDir, "request.json");
        File.WriteAllText(requestFile, JsonSerializer.Serialize(new { gamePath = game.Root }));

        AssertOk(RunRaw(["detect", "--request", requestFile], "").Envelope);
        AssertRefused(RunRaw(["detect", "--request", Path.Combine(TempDir, "missing.json")], "").Envelope, "BAD_REQUEST");
        AssertRefused(RunRaw(["detect", "--bogus"], "").Envelope, "BAD_REQUEST");
    }

    [TestMethod]
    public void RealProcess_ReadsStdinAndWritesOneEnvelopeToStdout()
    {
        // A game path with non-ASCII characters proves stdin/stdout are UTF-8.
        var game = GameFixture.CreateMono(Path.Combine(TempDir, "Spiele ü 日本"));
        var dll = Path.Combine(AppContext.BaseDirectory, "spectuner-sidecar.dll");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("detect");
        start.Environment["SPECTUNER_HOME"] = Home;

        using var process = Process.Start(start)!;
        process.StandardInput.Write(JsonSerializer.Serialize(new { gamePath = game.Root }));
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.IsTrue(process.WaitForExit(60_000));

        Assert.AreEqual(0, process.ExitCode, stderr);
        using var document = JsonDocument.Parse(stdout);
        var envelope = document.RootElement;
        AssertOk(envelope);
        Assert.AreEqual("detect", envelope.GetProperty("command").GetString());
        Assert.AreEqual("Fixture Game", envelope.GetProperty("data").GetProperty("game").GetProperty("name").GetString());
        Assert.IsFalse(Directory.Exists(Path.Combine(Home, "games")));
    }
}
