using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using SpecTuner.Sidecar.Compilation;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Code;

/// <summary>Which assemblies in <c>Managed/</c> are the game's own code.</summary>
public static class GameAssemblies
{
    /// <summary>Engine, framework and loader assemblies: not searched unless asked for by name.</summary>
    public static readonly string[] EngineAndFramework =
    [
        "UnityEngine.dll", "UnityEngine.*.dll", "Unity.*.dll",
        "System.dll", "System.*.dll", "mscorlib.dll", "netstandard.dll", "Microsoft.*.dll",
        "Mono.*.dll", "Newtonsoft.*.dll",
        "BepInEx*.dll", "0Harmony*.dll", "HarmonyX*.dll", "MonoMod.*.dll",
    ];

    public static bool IsEngineOrFramework(string fileName) =>
        EngineAndFramework.Any(p => FileSystemName.MatchesSimpleExpression(p, fileName, ignoreCase: true));

    /// <summary>Managed (.NET) assemblies in the game's Managed folder, as full paths.</summary>
    public static List<string> All(GameFacts facts) =>
        facts.ManagedDir is null || !Directory.Exists(facts.ManagedDir)
            ? []
            : Directory.GetFiles(facts.ManagedDir, "*.dll")
                .Where(p => PeFile.TryRead(p) is { HasValidMetadata: true })
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

    public static List<string> Own(GameFacts facts) =>
        All(facts).Where(p => !IsEngineOrFramework(Path.GetFileName(p))).ToList();

    /// <summary>One assembly by file name (with or without .dll) or assembly name, including engine ones.</summary>
    public static string? Find(GameFacts facts, string name)
    {
        var wanted = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        return All(facts).FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Equals(wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(PluginCompiler.AssemblyName(File.ReadAllBytes(p)), wanted, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Decompiles an assembly once into <c>&lt;state&gt;/decompiled/&lt;assemblyHash&gt;/</c>:
/// <c>types/*.cs</c> (one file per top-level type) and <c>index.json</c>.
/// </summary>
public static class DecompiledCache
{
    public sealed record Entry(CodeIndex Index, string Dir, bool FromCache)
    {
        public string TypeFile(TypeEntry type) => Path.Combine(Dir, "types", type.File);
    }

    public static Entry Load(GameState state, GameFacts facts, string assemblyPath)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assemblyPath)))[..16];
        var root = Path.Combine(state.Dir, "decompiled");
        var dir = Path.Combine(root, hash);
        var indexPath = Path.Combine(dir, "index.json");

        var cached = AtomicFile.ReadJson<CodeIndex>(indexPath);
        if (cached is { Version: CodeIndex.CurrentVersion })
            return new Entry(cached, dir, FromCache: true);

        // Build in a temp folder and move it into place, so a half-built cache is never used.
        var temp = Path.Combine(root, hash + ".building-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(temp, "types"));
        try
        {
            var index = Build(assemblyPath, facts.ManagedDir!, Path.Combine(temp, "types"));
            index.AssemblyHash = hash;
            AtomicFile.WriteJson(Path.Combine(temp, "index.json"), index);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.Move(temp, dir);
            return new Entry(index, dir, FromCache: false);
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    private static CodeIndex Build(string assemblyPath, string managedDir, string typesDir)
    {
        var clock = Stopwatch.StartNew();
        List<string> topLevel;
        string assemblyName;
        using (var module = new PEFile(assemblyPath))
        {
            var metadata = module.Metadata;
            assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            topLevel = metadata.TypeDefinitions
                .Select(metadata.GetTypeDefinition)
                .Where(t => t.GetDeclaringType().IsNil)
                .Select(t =>
                {
                    var ns = metadata.GetString(t.Namespace);
                    var name = metadata.GetString(t.Name);
                    return ns.Length == 0 ? name : ns + "." + name;
                })
                .Where(n => !n.Contains('<'))
                .ToList();
        }

        var files = new ConcurrentBag<(int Order, string File, string Text)>();
        var usedNames = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(
            Enumerable.Range(0, topLevel.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) },
            () => CreateDecompiler(assemblyPath, managedDir),
            (i, _, decompiler) =>
            {
                string text;
                try
                {
                    text = decompiler.DecompileTypeAsString(new FullTypeName(topLevel[i]));
                }
                catch (Exception ex)
                {
                    text = $"// SpecTuner could not decompile {topLevel[i]}: {ex.GetType().Name}: {ex.Message}\n";
                }
                var file = SafeFileName(topLevel[i]);
                var count = usedNames.AddOrUpdate(file, 1, (_, n) => n + 1);
                if (count > 1)
                    file += "_" + count;
                file += ".cs";
                File.WriteAllText(Path.Combine(typesDir, file), text, new UTF8Encoding(false));
                files.Add((i, file, text));
                return decompiler;
            },
            _ => { });

        var types = new List<TypeEntry>();
        foreach (var (_, file, text) in files.OrderBy(f => f.Order))
            types.AddRange(IndexFile(file, text));

        return new CodeIndex
        {
            Assembly = assemblyName,
            AssemblyFile = Path.GetFileName(assemblyPath),
            DecompileSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
            Types = types,
        };
    }

    private static CSharpDecompiler CreateDecompiler(string assemblyPath, string managedDir)
    {
        var module = new PEFile(assemblyPath);
        var resolver = new UniversalAssemblyResolver(assemblyPath, false, module.DetectTargetFrameworkId());
        resolver.AddSearchDirectory(managedDir);
        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false, ShowXmlDocumentation = false };
        return new CSharpDecompiler(module, resolver, settings);
    }

    private static string SafeFileName(string typeName)
    {
        var chars = typeName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray();
        return new string(chars);
    }

    /// <summary>Finds every type and member in one decompiled file, with line ranges and signatures.</summary>
    public static List<TypeEntry> IndexFile(string file, string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest));
        var result = new List<TypeEntry>();
        foreach (var type in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            result.Add(IndexType(file, type));
        foreach (var del in tree.GetRoot().DescendantNodes().OfType<DelegateDeclarationSyntax>().Where(d => d.Parent is not TypeDeclarationSyntax))
            result.Add(new TypeEntry
            {
                FullName = Qualify(del, del.Identifier.Text), Name = del.Identifier.Text + del.TypeParameterList, Kind = "delegate",
                Signature = Collapse(del.WithAttributeLists(default).ToString()), File = file,
                StartLine = Line(del.GetLocation().GetLineSpan().StartLinePosition), EndLine = Line(del.GetLocation().GetLineSpan().EndLinePosition),
            });
        return result;
    }

    private static TypeEntry IndexType(string file, BaseTypeDeclarationSyntax type)
    {
        var span = type.GetLocation().GetLineSpan();
        var entry = new TypeEntry
        {
            FullName = Qualify(type, type.Identifier.Text),
            Name = type.Identifier.Text + (type as TypeDeclarationSyntax)?.TypeParameterList,
            Kind = type switch
            {
                RecordDeclarationSyntax => "record",
                ClassDeclarationSyntax => "class",
                StructDeclarationSyntax => "struct",
                InterfaceDeclarationSyntax => "interface",
                EnumDeclarationSyntax => "enum",
                _ => "type",
            },
            Signature = Header(type),
            File = file,
            StartLine = Line(span.StartLinePosition),
            EndLine = Line(span.EndLinePosition),
        };

        IEnumerable<MemberDeclarationSyntax> members = type switch
        {
            TypeDeclarationSyntax t => t.Members.Where(m => m is not BaseTypeDeclarationSyntax),
            EnumDeclarationSyntax e => e.Members,
            _ => [],
        };
        foreach (var member in members)
        {
            var memberSpan = member.GetLocation().GetLineSpan();
            foreach (var name in MemberNames(member, type.Identifier.Text))
                entry.Members.Add(new MemberEntry
                {
                    Name = name,
                    Kind = MemberKind(member),
                    Signature = MemberSignature(member),
                    StartLine = Line(memberSpan.StartLinePosition),
                    EndLine = Line(memberSpan.EndLinePosition),
                });
        }
        return entry;
    }

    private static int Line(LinePosition position) => position.Line + 1;

    private static string Qualify(SyntaxNode node, string name)
    {
        var parts = new List<string> { name };
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            switch (parent)
            {
                case BaseTypeDeclarationSyntax t:
                    parts.Insert(0, t.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Insert(0, ns.Name.ToString());
                    break;
            }
        }
        return string.Join(".", parts);
    }

    private static IEnumerable<string> MemberNames(MemberDeclarationSyntax member, string typeName) => member switch
    {
        MethodDeclarationSyntax m => [m.Identifier.Text],
        ConstructorDeclarationSyntax => [typeName],
        DestructorDeclarationSyntax => ["~" + typeName],
        PropertyDeclarationSyntax p => [p.Identifier.Text],
        IndexerDeclarationSyntax => ["this[]"],
        EventDeclarationSyntax e => [e.Identifier.Text],
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.Select(v => v.Identifier.Text),
        OperatorDeclarationSyntax o => ["operator " + o.OperatorToken.Text],
        ConversionOperatorDeclarationSyntax c => ["operator " + c.Type],
        EnumMemberDeclarationSyntax e => [e.Identifier.Text],
        DelegateDeclarationSyntax d => [d.Identifier.Text],
        _ => [],
    };

    private static string MemberKind(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax => "method",
        ConstructorDeclarationSyntax => "constructor",
        DestructorDeclarationSyntax => "destructor",
        PropertyDeclarationSyntax => "property",
        IndexerDeclarationSyntax => "indexer",
        EventFieldDeclarationSyntax or EventDeclarationSyntax => "event",
        FieldDeclarationSyntax => "field",
        OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => "operator",
        EnumMemberDeclarationSyntax => "enum value",
        DelegateDeclarationSyntax => "delegate",
        _ => "member",
    };

    /// <summary>The declaration without attributes, bodies or initializers, on one line.</summary>
    public static string MemberSignature(MemberDeclarationSyntax member)
    {
        SyntaxNode stripped = member switch
        {
            BaseMethodDeclarationSyntax m => m.WithAttributeLists(default).WithBody(null).WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken)),
            PropertyDeclarationSyntax p => p.WithAttributeLists(default).WithInitializer(null).WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken))
                .WithAccessorList(p.AccessorList is null ? null : StripAccessors(p.AccessorList)),
            IndexerDeclarationSyntax i => i.WithAttributeLists(default).WithExpressionBody(null)
                .WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken))
                .WithAccessorList(i.AccessorList is null ? null : StripAccessors(i.AccessorList)),
            EventDeclarationSyntax e => e.WithAttributeLists(default).WithAccessorList(null),
            BaseFieldDeclarationSyntax f => f.WithAttributeLists(default).WithDeclaration(
                f.Declaration.WithVariables(SyntaxFactory.SeparatedList(f.Declaration.Variables.Select(v => v.WithInitializer(null))))),
            EnumMemberDeclarationSyntax e => e.WithAttributeLists(default),
            _ => member.WithAttributeLists(default),
        };
        var text = Collapse(stripped.ToString());
        return text.Length > 200 ? text[..200] + "..." : text;
    }

    private static AccessorListSyntax StripAccessors(AccessorListSyntax list) =>
        list.WithAccessors(SyntaxFactory.List(list.Accessors.Select(a => a.WithAttributeLists(default).WithBody(null).WithExpressionBody(null)
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))));

    private static string Header(BaseTypeDeclarationSyntax type)
    {
        var text = type.WithAttributeLists(default).ToString();
        var brace = text.IndexOf('{');
        var header = Collapse(brace >= 0 ? text[..brace] : text);
        return header.Length > 200 ? header[..200] + "..." : header;
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd(';').Trim();
}
