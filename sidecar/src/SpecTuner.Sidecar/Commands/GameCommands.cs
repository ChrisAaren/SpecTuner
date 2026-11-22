using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>Steps shared by the commands that work on a game.</summary>
public static class GameCommands
{
    /// <summary>
    /// The scope gate. With <paramref name="allowIfRecorded"/>, a game that is now out of scope
    /// (for example after a game update) still passes if SpecTuner has state for it, so the
    /// player can always list and undo what SpecTuner did.
    /// </summary>
    public static GameFacts RequireInScope(CommandRequest request, bool allowIfRecorded = false)
    {
        var verdict = GameInspector.Inspect(request.RequireString("gamePath"));
        if (verdict.Supported)
            return verdict.Facts;
        if (allowIfRecorded && verdict.Code != ErrorCodes.GameNotFound && GameState.For(verdict.Facts.FullPath).HasRecord)
            return verdict.Facts;
        throw verdict.ToException();
    }
}
