using Microsoft.CodeAnalysis;
using SpecTuner.Sidecar.Compilation;

namespace SpecTuner.Sidecar.Tests.Support;

/// <summary>
/// Compile-time stand-ins for Mono class libraries a Unity game may ship in Managed/
/// (Mono.Posix, Mono.CSharp, Mono.Data.Sqlite, System.Runtime.Remoting, System.IO.Ports)
/// and that the .NET runtime running the tests does not have. Only the names matter: the
/// safety check works on resolved symbols.
/// </summary>
public static class MonoStub
{
    private static readonly Lazy<byte[]> LazyAssembly = new(() =>
    {
        var references = new[] { "System.Runtime.dll", "System.Private.CoreLib.dll" }
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(UnityStub.RuntimeDirectory, f)))
            .ToList();
        var result = PluginCompiler.Compile("MonoStub", [new SourceFile("MonoStub.cs", Source)], references);
        if (!result.Success)
            throw new InvalidOperationException("Mono stub failed to compile: " + string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Assembly!;
    });

    public static byte[] Assembly => LazyAssembly.Value;

    private const string Source = """
        namespace Mono.Unix { public sealed class UnixFileInfo { public UnixFileInfo(string path) { } } }
        namespace Mono.Unix.Native { public static class Syscall { public static int chmod(string path, uint mode) { return 0; } } }
        namespace Mono.Posix { public static class Syscall { public static int unlink(string path) { return 0; } } }
        namespace Mono.CSharp { public sealed class Evaluator { public bool Run(string code) { return false; } } }
        namespace Mono.Data.Sqlite { public sealed class SqliteConnection { public SqliteConnection(string connectionString) { } } }
        namespace System.Runtime.Remoting { public static class RemotingConfiguration { public static void Configure(string filename) { } } }
        namespace System.IO.Ports { public sealed class SerialPort { public SerialPort(string portName) { } } }
        """;
}
