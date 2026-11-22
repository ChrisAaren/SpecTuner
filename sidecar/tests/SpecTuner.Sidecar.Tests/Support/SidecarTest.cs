using System.Text.Json;
using SpecTuner.Sidecar.Cli;

namespace SpecTuner.Sidecar.Tests.Support;

/// <summary>Gives each test its own temp folder and its own SPECTUNER_HOME.</summary>
public abstract class SidecarTest
{
    protected string TempDir { get; private set; } = "";
    protected string Home { get; private set; } = "";

    [TestInitialize]
    public void CreateTempHome()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "spectuner-tests", Guid.NewGuid().ToString("N")[..12]);
        Home = Path.Combine(TempDir, "home");
        Directory.CreateDirectory(TempDir);
        Environment.SetEnvironmentVariable("SPECTUNER_HOME", Home);
    }

    [TestCleanup]
    public void DeleteTempHome()
    {
        Environment.SetEnvironmentVariable("SPECTUNER_HOME", null);
        try
        {
            Directory.Delete(TempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    protected GameFixture CreateGame(string name = "Fixture Game") => GameFixture.CreateMono(TempDir, name);

    /// <summary>Runs a command through the real dispatcher, request on "stdin", envelope from "stdout".</summary>
    protected static JsonElement Run(string command, object request) => RunRaw([command], Json.Serialize(request)).Envelope;

    protected static (JsonElement Envelope, int ExitCode, string Stdout) RunRaw(string[] args, string stdin)
    {
        var stdout = new StringWriter();
        var exitCode = CommandHost.Run(args, new StringReader(stdin), stdout, new StringWriter());
        var text = stdout.ToString();
        using var document = JsonDocument.Parse(text);
        return (document.RootElement.Clone(), exitCode, text);
    }

    protected static string ErrorCode(JsonElement envelope) =>
        envelope.TryGetProperty("error", out var error) ? error.GetProperty("code").GetString()! : "(none)";

    protected static void AssertOk(JsonElement envelope)
    {
        Assert.IsTrue(envelope.GetProperty("ok").GetBoolean(), $"Expected ok:true, got {envelope}");
    }

    protected static void AssertRefused(JsonElement envelope, string code)
    {
        Assert.IsFalse(envelope.GetProperty("ok").GetBoolean(), $"Expected ok:false, got {envelope}");
        Assert.AreEqual(code, ErrorCode(envelope), envelope.ToString());
    }
}
