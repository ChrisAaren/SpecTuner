using System.Text.Json;
using SpecTuner.Sidecar.Commands;

namespace SpecTuner.Sidecar.Cli;

public static class CommandHost
{
    public static readonly IReadOnlyList<ICommand> Commands =
    [
        new DetectCommand(),
        new InstallCommand(),
        new ChangesCommand(),
        new RevertCommand(),
        new SetQualityCommand(),
        new LaunchAndBenchmarkCommand(),
        new ReadLogsCommand(),
        new SearchCodeCommand(),
        new ReadCodeCommand(),
        new BuildPluginCommand(),
        new ReapplyCommand(),
        new PlayCommand(),
        new DiagnoseCommand(),
    ];

    /// <summary>
    /// Runs one command and writes exactly one envelope to <paramref name="stdout"/>.
    /// Returns 0 when the command was handled (including refusals), 1 on an unexpected internal error.
    /// </summary>
    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var commandName = args.Length > 0 ? args[0] : "";
        int exitCode = 0;
        CommandResult result;
        try
        {
            result = Dispatch(commandName, args.Skip(1).ToArray(), stdin);
        }
        catch (SidecarException ex)
        {
            result = CommandResult.Failure(ex.Code, ex.Message, ex.Detail, ex.ResultData);
        }
        catch (Exception ex)
        {
            stderr.WriteLine(ex.ToString());
            result = CommandResult.Failure(
                ErrorCodes.InternalError,
                "Something unexpected went wrong inside SpecTuner.",
                $"{ex.GetType().Name}: {ex.Message}");
            exitCode = 1;
        }

        var envelope = new Envelope
        {
            Ok = result.Ok,
            Command = commandName,
            Summary = result.Ok ? result.Summary : null,
            Error = result.Ok ? null : result.Error,
            Data = result.Data,
        };
        stdout.WriteLine(Json.Serialize(envelope));
        stdout.Flush();
        return exitCode;
    }

    private static CommandResult Dispatch(string commandName, string[] options, TextReader stdin)
    {
        var known = string.Join(", ", Commands.Select(c => c.Name));
        if (commandName.Length == 0 || commandName.StartsWith('-'))
            throw new SidecarException(ErrorCodes.UnknownCommand, "No command was given.",
                $"Usage: spectuner-sidecar <command> [--request <file.json>]. Commands: {known}");

        var command = Commands.FirstOrDefault(c => c.Name == commandName)
            ?? throw new SidecarException(ErrorCodes.UnknownCommand, $"\"{commandName}\" is not a command SpecTuner knows.",
                $"Commands: {known}");

        var text = ReadRequestText(options, stdin);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
        catch (JsonException ex)
        {
            throw new SidecarException(ErrorCodes.InvalidJson, "The request could not be read.", ex.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new SidecarException(ErrorCodes.BadRequest, "The request must be a JSON object.");
            return command.Execute(new CommandRequest(document.RootElement));
        }
    }

    private static string ReadRequestText(string[] options, TextReader stdin)
    {
        if (options.Length == 0)
            return stdin.ReadToEnd();

        if (options.Length == 2 && options[0] == "--request")
        {
            try
            {
                return File.ReadAllText(options[1]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                throw new SidecarException(ErrorCodes.BadRequest, "The request file could not be read.", ex.Message);
            }
        }

        throw new SidecarException(ErrorCodes.BadRequest, "Unexpected command-line options.",
            $"Got: {string.Join(' ', options)}. Expected nothing (request on stdin) or --request <file.json>.");
    }
}
