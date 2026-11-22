using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SpecTuner.Sidecar.Compilation;

/// <summary>
/// One refused construct. <see cref="Target"/> is a namespace ("System.Net.*", which includes
/// the namespace itself and everything below it), a type ("System.IO.File") or a member
/// ("System.Environment.Exit").
/// </summary>
public sealed record DenyRule(string Target, string Category, string Reason);

public sealed record SafetyViolation(string Construct, string Category, string Reason, string File, int Line, int Column);

/// <summary>
/// Damage limitation for generated mods: refuses code that reaches outside the game.
/// Works on resolved symbols from the semantic model, so using-aliases and fully
/// qualified names are caught. It does not catch code reached only through reflection
/// strings (Type.GetType("...").GetMethod("...").Invoke), which stays allowed because
/// mods need reflection to read game fields.
/// </summary>
public static class SafetyCheck
{
    private const string Programs = "A mod must not start other programs.";
    private const string Network = "A mod must not use the network or open web pages.";
    private const string Registry = "A mod must not read or change the Windows registry.";
    private const string Native = "A mod must not call native (non-.NET) code or work with raw memory.";
    private const string Loading = "A mod must not load or generate other code.";
    private const string Files = "A mod must not read or write files; it changes game behaviour and logs through BepInEx (Logger).";
    private const string Environment = "A mod must not end the game process or change environment variables.";
    private const string Threads = "A mod must not start threads, timers or background tasks; use Update() or a Unity coroutine instead.";

    public static readonly DenyRule[] Rules =
    [
        new("System.Diagnostics.Process", "process start", Programs),
        new("System.Diagnostics.ProcessStartInfo", "process start", Programs),
        new("System.Net.*", "networking", Network),
        new("UnityEngine.Networking.*", "networking", Network),
        new("UnityEngine.WWW", "networking", Network),
        new("UnityEngine.WWWForm", "networking", Network),
        new("UnityEngine.Application.OpenURL", "networking", Network),
        new("Microsoft.Win32.*", "registry", Registry),
        new("System.Runtime.InteropServices.DllImportAttribute", "native code", Native),
        new("System.Runtime.InteropServices.Marshal", "native code", Native),
        new("System.Runtime.InteropServices.NativeLibrary", "native code", Native),
        new("System.Reflection.Assembly.Load", "loading code", Loading),
        new("System.Reflection.Assembly.LoadFrom", "loading code", Loading),
        new("System.Reflection.Assembly.LoadFile", "loading code", Loading),
        new("System.Reflection.Assembly.UnsafeLoadFrom", "loading code", Loading),
        new("System.Reflection.Assembly.ReflectionOnlyLoad", "loading code", Loading),
        new("System.Reflection.Assembly.ReflectionOnlyLoadFrom", "loading code", Loading),
        new("System.Reflection.Emit.*", "loading code", Loading),
        new("System.Runtime.Loader.*", "loading code", Loading),
        new("System.CodeDom.Compiler.*", "loading code", Loading),
        new("Microsoft.CSharp.*", "loading code", Loading),
        new("System.AppDomain.Load", "loading code", Loading),
        new("System.AppDomain.ExecuteAssembly", "loading code", Loading),
        new("System.AppDomain.ExecuteAssemblyByName", "loading code", Loading),
        new("System.AppDomain.CreateDomain", "loading code", Loading),
        new("System.AppDomain.CreateInstanceFrom", "loading code", Loading),
        new("System.AppDomain.CreateInstanceFromAndUnwrap", "loading code", Loading),
        new("System.Activator.CreateInstanceFrom", "loading code", Loading),
        new("System.IO.File", "file access", Files),
        new("System.IO.Directory", "file access", Files),
        new("System.IO.FileInfo", "file access", Files),
        new("System.IO.DirectoryInfo", "file access", Files),
        new("System.IO.FileSystemInfo", "file access", Files),
        new("System.IO.FileStream", "file access", Files),
        new("System.IO.StreamWriter", "file access", Files),
        new("System.IO.StreamReader", "file access", Files),
        new("System.IO.FileSystemWatcher", "file access", Files),
        new("System.IO.DriveInfo", "file access", Files),
        new("System.Environment.Exit", "environment", Environment),
        new("System.Environment.FailFast", "environment", Environment),
        new("System.Environment.SetEnvironmentVariable", "environment", Environment),
        new("System.Threading.Thread", "threads", Threads),
        new("System.Threading.ThreadPool", "threads", Threads),
        new("System.Threading.Timer", "threads", Threads),
        new("System.Timers.Timer", "threads", Threads),
        new("System.Threading.Tasks.Task.Run", "threads", Threads),
        new("System.Threading.Tasks.TaskFactory.StartNew", "threads", Threads),
    ];

    /// <summary>Refused language features, found from the syntax.</summary>
    private static readonly (string Construct, string Category, string Reason)[] Syntax =
    [
        ("unsafe", "native code", Native),
        ("extern", "native code", Native),
        ("dynamic", "loading code", "A mod must not use 'dynamic': it hides which code is called from the safety check."),
    ];

    public const int MaxViolations = 10;

    public static List<SafetyViolation> Check(CSharpCompilation compilation)
    {
        var found = new List<SafetyViolation>();
        var seen = new HashSet<(string, string, int)>();

        void Add(string construct, string category, string reason, SyntaxNode node)
        {
            var span = node.GetLocation().GetLineSpan();
            var line = span.StartLinePosition.Line + 1;
            if (seen.Add((construct, span.Path, line)))
                found.Add(new SafetyViolation(construct, category, reason, span.Path, line, span.StartLinePosition.Character + 1));
        }

        foreach (var tree in compilation.SyntaxTrees.Where(t => t.FilePath != "<polyfill>"))
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.UnsafeKeyword))
                    Add(Syntax[0].Construct, Syntax[0].Category, Syntax[0].Reason, token.Parent!);
                else if (token.IsKind(SyntaxKind.ExternKeyword) && token.Parent is not ExternAliasDirectiveSyntax)
                    Add(Syntax[1].Construct, Syntax[1].Category, Syntax[1].Reason, token.Parent!);
            }

            foreach (var node in root.DescendantNodes())
            {
                ISymbol? symbol = node switch
                {
                    SimpleNameSyntax or ImplicitObjectCreationExpressionSyntax =>
                        model.GetSymbolInfo(node).Symbol ?? model.GetSymbolInfo(node).CandidateSymbols.FirstOrDefault(),
                    _ => null,
                };
                if (symbol is null && node is SimpleNameSyntax name)
                    symbol = model.GetAliasInfo(name);
                if (symbol is null)
                    continue;

                // In File.WriteAllText, report the member, not also the type name in front of it.
                SyntaxNode target = node.Parent is MemberAccessExpressionSyntax inner && inner.Name == node ? inner : node;
                if (symbol is INamedTypeSymbol && target.Parent is MemberAccessExpressionSyntax outer && outer.Expression == target)
                    continue;

                if (symbol is ITypeSymbol { TypeKind: TypeKind.Dynamic })
                {
                    Add(Syntax[2].Construct, Syntax[2].Category, Syntax[2].Reason, node);
                    continue;
                }
                var (rule, construct) = Match(symbol);
                if (rule is not null)
                    Add(construct, rule.Category, rule.Reason, node);
            }
        }
        return found.OrderBy(v => v.File).ThenBy(v => v.Line).Take(MaxViolations).ToList();
    }

    /// <summary>The rule a symbol falls under, and how to name the construct in the message.</summary>
    public static (DenyRule? Rule, string Construct) Match(ISymbol symbol)
    {
        switch (symbol)
        {
            case IAliasSymbol alias:
                return Match(alias.Target);
            case INamedTypeSymbol type:
                return (MatchType(type), TypeName(type));
            case IMethodSymbol method:
            {
                method = method.ReducedFrom ?? method;
                var type = method.ContainingType;
                if (type is null)
                    return (null, "");
                var memberName = method.MethodKind == MethodKind.Constructor ? TypeName(type) : TypeName(type) + "." + method.Name;
                return (MatchMember(type, method.Name) ?? MatchType(type), memberName);
            }
            case IPropertySymbol or IFieldSymbol or IEventSymbol:
            {
                var type = symbol.ContainingType;
                return type is null ? (null, "") : (MatchMember(type, symbol.Name) ?? MatchType(type), TypeName(type) + "." + symbol.Name);
            }
            default:
                return (null, "");
        }
    }

    private static DenyRule? MatchMember(INamedTypeSymbol type, string member)
    {
        var full = TypeName(type) + "." + member;
        return Rules.FirstOrDefault(r => r.Target == full);
    }

    private static DenyRule? MatchType(INamedTypeSymbol type)
    {
        for (var t = type; t is not null; t = t.ContainingType)
        {
            var full = TypeName(t);
            var ns = t.ContainingNamespace?.ToDisplayString() ?? "";
            var rule = Rules.FirstOrDefault(r =>
                r.Target == full
                || r.Target.EndsWith(".*", StringComparison.Ordinal)
                   && (ns == r.Target[..^2] || ns.StartsWith(r.Target[..^1], StringComparison.Ordinal)));
            if (rule is not null)
                return rule;
        }
        return null;
    }

    /// <summary>Namespace-qualified name without generic arguments: <c>System.Collections.Generic.List</c>.</summary>
    private static string TypeName(INamedTypeSymbol type)
    {
        var name = type.OriginalDefinition.Name;
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            name = outer.Name + "." + name;
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
        return ns + name;
    }
}
