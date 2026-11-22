using System.ComponentModel;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Loader;
using SpecTuner.Sidecar.Platform;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>
/// Starts the game for the player, with or without SpecTuner's changes, and returns at once. Does
/// not wait, measure, or revert anything. <c>withChanges: false</c> switches the loader off for this
/// one run without changing any file (Windows: Doorstop's <c>--doorstop-enabled false</c> argument;
/// Linux and macOS: the game starts without the loader's environment).
/// </summary>
public sealed class PlayCommand : ICommand
{
    public string Name => "play";

    public CommandResult Execute(CommandRequest request)
    {
        var withChanges = request.OptionalBool("withChanges", fallback: true);
        var facts = GameCommands.RequireInScope(request);
        var state = GameState.For(facts.FullPath);
        var platform = Platforms.Current;
        var kit = platform.Loader;
        var root = facts.FullPath;
        var name = facts.Name ?? "The game";

        // Not while another SpecTuner command is changing the game.
        using var gameLock = state.Lock();
        GameProcesses.EnsureNotRunning(facts);

        var loaderPresent = File.Exists(GameTree.Full(root, LoaderKit.PreloaderDll)) && File.Exists(GameTree.Full(root, kit.DoorstopLibrary));
        var loaderActive = withChanges && loaderPresent;
        if (loaderActive)
            GameTree.DeleteFile(GameTree.Full(root, OurFiles.BenchmarkRequestFile)); // a leftover one-shot request would start a benchmark

        var spec = !loaderPresent ? LaunchSpec.Plain(facts.ExePath!)
            : withChanges ? kit.WithLoader(facts, Environment.GetEnvironmentVariable)
            : kit.WithoutLoader(facts);

        try
        {
            using var process = platform.Processes.StartDetached(spec, root)
                ?? throw new InvalidOperationException("No process was started.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not start the game: {ex.Message}");
            return CommandResult.Failure(ErrorCodes.GameFailedToStart,
                $"{name} couldn't be started. Nothing was changed.",
                ex.Message,
                new { started = false, withChanges, loaderActive = false, steamLaunchOption = (string?)null, note = (string?)null });
        }

        var steamLaunchOption = loaderPresent && LooksLikeSteamGame(root) ? kit.SteamLaunchOption(root) : null;
        string? note = null;
        if (withChanges && !loaderPresent)
            note = "SpecTuner hasn't added anything to this game yet, so it started as usual.";
        else if (loaderActive && steamLaunchOption is not null)
            note = "If Steam restarts the game by itself, it starts without your changes. To have them when you start the game from Steam, " +
                   "add the launch option shown here to the game's launch options in Steam.";

        return CommandResult.Success(
            loaderActive ? $"Started {name} with your changes."
                : withChanges ? $"Started {name}."
                : $"Started {name} without SpecTuner's changes.",
            new { started = true, withChanges, loaderActive, steamLaunchOption, note });
    }

    /// <summary>A game installed by Steam sits in a <c>steamapps/common</c> folder.</summary>
    public static bool LooksLikeSteamGame(string gameRoot) =>
        gameRoot.Replace('\\', '/').Contains("/steamapps/common/", StringComparison.OrdinalIgnoreCase);
}
