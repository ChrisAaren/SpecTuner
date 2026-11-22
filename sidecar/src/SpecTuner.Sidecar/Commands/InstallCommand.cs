using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>Records the baseline (once), then installs BepInEx and the probe plugin. Safe to run again.</summary>
public sealed class InstallCommand : ICommand
{
    public string Name => "install";

    public CommandResult Execute(CommandRequest request)
    {
        var facts = GameCommands.RequireInScope(request);
        var state = GameState.For(facts.FullPath);
        using var _ = state.Lock();
        GameProcesses.EnsureNotRunning(facts);

        var game = GameSetup.EnsureInstalled(facts, state);
        var loader = game.Loader;
        var probe = game.Probe;

        var summary = (loader, probe.AlreadyInstalled) switch
        {
            ({ AlreadyInstalled: true }, true) => "Everything SpecTuner needs was already installed. Nothing needed to change.",
            ({ Repaired: true }, _) => "Some SpecTuner files were missing and have been put back.",
            ({ Ours: false }, _) => "This game already had the BepInEx mod loader, so SpecTuner uses it (and will never remove it) and added its helper. No original game file was changed.",
            _ => "SpecTuner installed the BepInEx mod loader and its helper. No original game file was changed.",
        };

        return CommandResult.Success(summary, new
        {
            baseline = new { created = game.BaselineCreated, files = game.Baseline.FileCount, totalBytes = game.Baseline.TotalBytes },
            loader = new
            {
                installed = true,
                ours = loader.Ours,
                version = loader.Version,
                alreadyInstalled = loader.AlreadyInstalled,
                changeId = loader.ChangeId,
                filesAdded = loader.FilesAdded,
            },
            probe = new { installed = probe.Installed, changeId = probe.ChangeId, alreadyInstalled = probe.AlreadyInstalled },
        });
    }
}
