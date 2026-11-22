using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Loader;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>Looks at a game folder and says whether SpecTuner can work with it. Makes no changes anywhere.</summary>
public sealed class DetectCommand : ICommand
{
    public string Name => "detect";

    public CommandResult Execute(CommandRequest request)
    {
        var verdict = GameInspector.Inspect(request.RequireString("gamePath"));
        if (!verdict.Supported)
            throw verdict.ToException();

        var facts = verdict.Facts;
        var state = GameState.For(facts.FullPath);
        var record = state.LoadRecord();
        var baseline = state.LoadBaseline();
        var ledger = baseline is null ? null : Ledger.Load(state, baseline);
        var loader = LoaderInstaller.Inspect(facts.FullPath, record);

        return CommandResult.Success(
            $"{facts.Name} is a Unity {facts.UnityVersion ?? ""} game that SpecTuner can work with.".Replace("  ", " "),
            new
            {
                supported = true,
                game = facts,
                loader = new { installed = loader.Installed, ours = loader.Ours, version = loader.Version },
                probe = new { installed = ProbeInstaller.IsInstalled(ledger) },
                activeChanges = ledger?.Live.Count() ?? 0,
            });
    }
}
