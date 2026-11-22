namespace SpecTuner.Sidecar.Cli;

/// <summary>One sidecar command. Adding a command means adding one class and registering it in <see cref="CommandHost"/>.</summary>
public interface ICommand
{
    string Name { get; }

    CommandResult Execute(CommandRequest request);
}
