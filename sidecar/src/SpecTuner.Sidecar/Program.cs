using System.Text;
using SpecTuner.Sidecar.Cli;

// Read and write UTF-8 explicitly: the console default on Windows is an OEM
// code page, which would mangle game paths with non-ASCII characters.
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
return CommandHost.Run(args, stdin, stdout, Console.Error);
