using System.Text.Json;
using System.Text.Json.Nodes;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>
/// "Apply again": puts a past change back as a new change. A quality change goes through set-quality's
/// code with the settings it recorded; a mod is rebuilt from its saved source through build-plugin's code
/// (checks, compile against the game as it is now, install, test launch).
/// </summary>
public sealed class ReapplyCommand : ICommand
{
    public string Name => "reapply";

    public CommandResult Execute(CommandRequest request)
    {
        var changeId = request.RequireString("changeId");
        var test = request.OptionalBool("test", fallback: true);

        var facts = GameCommands.RequireInScope(request);
        var state = GameState.For(facts.FullPath);
        using var _ = state.Lock();
        GameProcesses.EnsureNotRunning(facts);

        var entry = PastChanges.Find(state, changeId)
            ?? throw new SidecarException(ErrorCodes.ChangeNotFound, "SpecTuner couldn't find that change.", $"No change with id {changeId}.");
        if (entry.Kind is not (ChangeKinds.Quality or ChangeKinds.Mod))
            throw new SidecarException(ErrorCodes.CannotReapply,
                $"\"{entry.Title}\" is set up automatically whenever it is needed, so it isn't put back on its own.");

        var past = PastChanges.Describe(state, changeId);
        if (past is null)
            throw new SidecarException(ErrorCodes.ChangeStillActive, $"\"{entry.Title}\" is still in place, so there is nothing to put back.");
        var copy = PastChanges.InPlaceCopyOf(state, changeId);
        if (copy is not null)
            throw new SidecarException(ErrorCodes.ChangeStillActive,
                $"\"{entry.Title}\" has already been put back and is in place.", $"In place as {copy.Id}.");
        if (!past.CanReapply)
            throw new SidecarException(ErrorCodes.CannotReapply, past.WhyNot!);

        CommandResult result;
        if (entry.Kind == ChangeKinds.Quality)
        {
            result = SetQualityCommand.Apply(facts, state, entry.Settings!.Value, entry.Title, entry.Description, reappliedFrom: entry.Id);
        }
        else
        {
            var sources = BuildPluginCommand.SavedSources(state, past.ModName!, entry.Id)!;
            var prepared = BuildPluginCommand.Prepare(facts, past.ModName!, sources);
            result = BuildPluginCommand.Install(facts, state, prepared, entry.Title, entry.Description, sources, [], test, reappliedFrom: entry.Id);
        }
        return WithReappliedFrom(result, entry);
    }

    /// <summary>The underlying command's result, with <c>reappliedFrom</c> added to its data and the summary saying what was put back.</summary>
    private static CommandResult WithReappliedFrom(CommandResult result, ChangeEntry entry)
    {
        var data = JsonSerializer.SerializeToNode(result.Data, Json.Options) as JsonObject ?? new JsonObject();
        data["reappliedFrom"] = entry.Id;
        return result.Ok
            ? CommandResult.Success($"Put back \"{entry.Title}\". {result.Summary}", data)
            : CommandResult.Failure(result.Error!.Code, result.Error.Message, result.Error.Detail, data);
    }
}
