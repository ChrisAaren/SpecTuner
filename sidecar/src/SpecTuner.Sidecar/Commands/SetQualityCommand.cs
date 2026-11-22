using System.Text.Json;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Quality;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>Merges quality overrides into spectuner.quality.json as one "quality" change. The probe applies them at the next launch.</summary>
public sealed class SetQualityCommand : ICommand
{
    public string Name => "set-quality";

    public CommandResult Execute(CommandRequest request)
    {
        if (!request.Root.TryGetProperty("settings", out var settings))
            throw new SidecarException(ErrorCodes.BadRequest, "The request needs \"settings\".", "Valid settings: " + QualitySettingsFile.ValidKeys);

        var facts = GameCommands.RequireInScope(request);
        var state = GameState.For(facts.FullPath);
        using var _ = state.Lock();
        GameProcesses.EnsureNotRunning(facts);
        return Apply(facts, state, settings, request.OptionalString("title"), request.OptionalString("description"), reappliedFrom: null);
    }

    /// <summary>
    /// What set-quality does once the lock is held and the game is known not to be running; reapply uses it too.
    /// The settings object is recorded on the change so it can be applied again later.
    /// </summary>
    public static CommandResult Apply(GameFacts facts, GameState state, JsonElement settings, string? requestedTitle, string? requestedDescription, string? reappliedFrom)
    {
        // Validate before installing anything.
        var current = QualitySettingsFile.Read(facts.FullPath);
        var merged = QualitySettingsFile.Merge(current, settings, QualityLevelCount(facts.FullPath));
        if (QualitySettingsFile.SameAs(current, merged))
            return CommandResult.Success("Those settings were already in place. Nothing changed.", new { changeId = (string?)null, overrides = merged });

        var game = GameSetup.EnsureInstalled(facts, state);
        var changed = settings.EnumerateObject().Select(p => p.Name).ToList();
        var title = requestedTitle ?? "Change graphics settings";
        var description = requestedDescription ?? Describe(settings);

        var change = game.Ledger.Begin(ChangeKinds.Quality, title, description);
        change.Settings = settings.Clone();
        change.ReappliedFrom = reappliedFrom;
        try
        {
            QualitySettingsFile.Write(facts.FullPath, merged);
            game.Ledger.Commit(change, [OurFiles.QualityFile]);
        }
        catch
        {
            game.Ledger.Abandon(change, [OurFiles.QualityFile]);
            throw;
        }

        return CommandResult.Success($"Saved: {title}. It takes effect the next time the game starts.",
            new { changeId = change.Id, changed, overrides = merged });
    }

    /// <summary>Number of quality levels, from the probe's last settings report, if there is one.</summary>
    private static int? QualityLevelCount(string gameRoot)
    {
        var path = GameTree.Full(gameRoot, OurFiles.ProbeOutputDir + "/settings.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("qualityLevelNames", out var names) && names.ValueKind == JsonValueKind.Array
                ? names.GetArrayLength()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Describe(JsonElement settings) =>
        string.Join("; ", settings.EnumerateObject().Select(p =>
            p.Value.ValueKind == JsonValueKind.Null ? $"{p.Name}: back to the game's own value" : $"{p.Name}: {p.Value}")) + ".";
}
