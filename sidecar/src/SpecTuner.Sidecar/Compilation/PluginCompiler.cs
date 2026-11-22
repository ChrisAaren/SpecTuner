using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Compilation;

public sealed record SourceFile(string Path, string Text);

public sealed record CompileError(
    string Id, string File, int Line, int Column, string Message,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Hint = null);

public sealed class CompileResult
{
    public byte[]? Assembly { get; init; }
    /// <summary>At most <see cref="PluginCompiler.MaxErrors"/> errors; <see cref="ErrorCount"/> is the full count.</summary>
    public List<CompileError> Errors { get; init; } = [];
    public int ErrorCount { get; init; }
    public bool Success => Assembly is not null;
}

/// <summary>
/// Compiles a class library in-process against a game's own assemblies
/// (<c>&lt;Name&gt;_Data/Managed/*.dll</c>) and BepInEx's core dlls. Used for the
/// probe plugin now and for generated mods (build-plugin) later.
/// </summary>
public static class PluginCompiler
{
    public const int MaxErrors = 15;

    /// <summary>
    /// C# 9: the newest version Unity's own compiler supports (2020.2+), so the
    /// features used are ones Unity's Mono runtime is known to handle. Newer
    /// versions add features that need runtime support Unity lacks (e.g. static
    /// abstract interface members, ref fields).
    /// </summary>
    public const LanguageVersion Language = LanguageVersion.CSharp9;

    // C# 9 'init' accessors and records need this type; older Unity mscorlibs lack it.
    private const string IsExternalInitPolyfill =
        "namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }";

    public static CompileResult Compile(string assemblyName, IReadOnlyList<SourceFile> sources, IReadOnlyList<MetadataReference> references) =>
        Emit(CreateCompilation(assemblyName, sources, references));

    public static CSharpCompilation CreateCompilation(string assemblyName, IReadOnlyList<SourceFile> sources, IReadOnlyList<MetadataReference> references)
    {
        var parseOptions = new CSharpParseOptions(Language);
        var trees = sources
            .Select(s => CSharpSyntaxTree.ParseText(s.Text, parseOptions, s.Path))
            .Append(CSharpSyntaxTree.ParseText(IsExternalInitPolyfill, parseOptions, "<polyfill>"))
            .ToList();

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithPlatform(Microsoft.CodeAnalysis.Platform.AnyCpu)
            .WithDeterministic(true)
            .WithAllowUnsafe(false)
            .WithNullableContextOptions(NullableContextOptions.Disable)
            // Unity's mscorlib is 4.x while BepInEx 5 references 2.0: let references unify like .NET Framework does.
            .WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default)
            // Import private and internal members of the game's assemblies too. They stay
            // inaccessible (the compiler still refuses to use them), but direct use now gives
            // CS0122 "inaccessible" naming the real member instead of CS1061 "does not
            // contain a definition", so the hint can say the member exists and how to reach it.
            .WithMetadataImportOptions(MetadataImportOptions.All);

        return CSharpCompilation.Create(assemblyName, trees, references, options);
    }

    public static CompileResult Emit(CSharpCompilation compilation)
    {
        using var output = new MemoryStream();
        var emit = compilation.Emit(output);

        if (emit.Success)
            return new CompileResult { Assembly = output.ToArray() };

        var errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return new CompileResult
        {
            ErrorCount = errors.Count,
            Errors = errors.Take(MaxErrors).Select(ToError).ToList(),
        };
    }

    /// <summary>Errors only, capped, each with a hint when the mistake is a common one.</summary>
    public static (List<CompileError> Errors, int Count) Errors(CSharpCompilation compilation)
    {
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return (errors.Take(MaxErrors).Select(d => ToError(d, PrivateMemberHint(compilation, d) ?? MissingUsingHint(compilation, d))).ToList(), errors.Count);
    }

    /// <summary>Namespaces of well-known types a model often uses without the using. Key: simple name, or name`arity when the arity decides.</summary>
    private static readonly Dictionary<string, string> WellKnownTypes = new(StringComparer.Ordinal)
    {
        ["HashSet"] = "System.Collections.Generic", ["List"] = "System.Collections.Generic", ["Dictionary"] = "System.Collections.Generic",
        ["Queue"] = "System.Collections.Generic", ["Stack"] = "System.Collections.Generic", ["KeyValuePair"] = "System.Collections.Generic",
        ["SortedDictionary"] = "System.Collections.Generic", ["SortedList"] = "System.Collections.Generic", ["LinkedList"] = "System.Collections.Generic",
        ["IList"] = "System.Collections.Generic", ["ICollection"] = "System.Collections.Generic", ["IDictionary"] = "System.Collections.Generic",
        ["IReadOnlyList"] = "System.Collections.Generic", ["IEnumerable`1"] = "System.Collections.Generic", ["IEnumerator`1"] = "System.Collections.Generic",
        ["IEnumerable`0"] = "System.Collections", ["IEnumerator`0"] = "System.Collections", ["ArrayList"] = "System.Collections", ["Hashtable"] = "System.Collections",
        ["Enumerable"] = "System.Linq",
        ["StringBuilder"] = "System.Text", ["Encoding"] = "System.Text",
        ["Action"] = "System", ["Func"] = "System", ["Math"] = "System", ["Exception"] = "System", ["Type"] = "System",
        ["DateTime"] = "System", ["TimeSpan"] = "System", ["Array"] = "System", ["Convert"] = "System", ["Guid"] = "System",
        ["Tuple"] = "System", ["Delegate"] = "System", ["EventHandler"] = "System", ["Serializable"] = "System",
        ["ArgumentException"] = "System", ["InvalidOperationException"] = "System", ["NullReferenceException"] = "System",
        ["MethodInfo"] = "System.Reflection", ["FieldInfo"] = "System.Reflection", ["PropertyInfo"] = "System.Reflection",
        ["BindingFlags"] = "System.Reflection", ["MemberInfo"] = "System.Reflection", ["ConstructorInfo"] = "System.Reflection",
        ["ParameterInfo"] = "System.Reflection",
        ["Keyboard"] = "UnityEngine.InputSystem", ["Mouse"] = "UnityEngine.InputSystem", ["Gamepad"] = "UnityEngine.InputSystem",
        ["ManualLogSource"] = "BepInEx.Logging",
        ["ConfigEntry"] = "BepInEx.Configuration", ["ConfigFile"] = "BepInEx.Configuration",
        ["BaseUnityPlugin"] = "BepInEx", ["BepInPlugin"] = "BepInEx", ["BepInDependency"] = "BepInEx", ["BepInProcess"] = "BepInEx",
        ["Harmony"] = "HarmonyLib", ["HarmonyPatch"] = "HarmonyLib", ["HarmonyPrefix"] = "HarmonyLib", ["HarmonyPostfix"] = "HarmonyLib",
        ["AccessTools"] = "HarmonyLib", ["Traverse"] = "HarmonyLib", ["CodeInstruction"] = "HarmonyLib",
        ["MonoBehaviour"] = "UnityEngine", ["GameObject"] = "UnityEngine", ["Transform"] = "UnityEngine", ["Vector2"] = "UnityEngine",
        ["Vector3"] = "UnityEngine", ["Quaternion"] = "UnityEngine", ["Mathf"] = "UnityEngine", ["Time"] = "UnityEngine",
        ["Input"] = "UnityEngine", ["KeyCode"] = "UnityEngine", ["Debug"] = "UnityEngine", ["Camera"] = "UnityEngine",
        ["Rigidbody"] = "UnityEngine", ["Coroutine"] = "UnityEngine", ["WaitForSeconds"] = "UnityEngine", ["Application"] = "UnityEngine",
        ["QualitySettings"] = "UnityEngine", ["Screen"] = "UnityEngine", ["Component"] = "UnityEngine",
        ["SceneManager"] = "UnityEngine.SceneManagement", ["Scene"] = "UnityEngine.SceneManagement",
    };

    /// <summary>LINQ extension methods: missing on a sequence (CS1061), they mean "using System.Linq;" is missing.</summary>
    private static readonly HashSet<string> LinqMethods = new(StringComparer.Ordinal)
    {
        "Where", "Select", "SelectMany", "Any", "All", "First", "FirstOrDefault", "Last", "LastOrDefault", "Single", "SingleOrDefault",
        "ToList", "ToArray", "ToDictionary", "ToHashSet", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "GroupBy",
        "Distinct", "Skip", "Take", "Sum", "Min", "Max", "Average", "Count", "Contains", "Concat", "Reverse", "Cast", "OfType",
        "ElementAt", "Aggregate", "Except", "Intersect", "Union", "Zip", "DefaultIfEmpty", "SequenceEqual",
    };

    /// <summary>
    /// For an unknown type or name (CS0246, CS0103, CS0305, CS0308) that is a well-known type: the exact using to add.
    /// For a LINQ method missing on a sequence (CS1061): "using System.Linq;". Null otherwise.
    /// </summary>
    public static string? MissingUsingHint(CSharpCompilation compilation, Diagnostic d)
    {
        if (d.Location.SourceTree is not { } tree)
            return null;
        var node = tree.GetRoot().FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);

        if (d.Id == "CS1061")
        {
            if (node is not SimpleNameSyntax member || !LinqMethods.Contains(member.Identifier.ValueText)
                || member.Parent is not MemberAccessExpressionSyntax access || access.Name != member)
                return null;
            var receiver = compilation.GetSemanticModel(tree).GetTypeInfo(access.Expression).Type;
            const string sequence = "System.Collections.Generic.IEnumerable<T>";
            var isSequence = receiver is IArrayTypeSymbol
                || receiver?.OriginalDefinition.ToDisplayString() == sequence
                || receiver?.AllInterfaces.Any(i => i.OriginalDefinition.ToDisplayString() == sequence) == true;
            return isSequence ? $"'{member.Identifier.ValueText}' is a LINQ method: add 'using System.Linq;' at the top of the file." : null;
        }

        if (d.Id is not ("CS0246" or "CS0103" or "CS0305" or "CS0308"))
            return null;
        var name = node switch
        {
            SimpleNameSyntax simple => simple,
            QualifiedNameSyntax qualified => qualified.Right,
            _ => node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().FirstOrDefault(),
        };
        if (name is null)
            return null;
        var text = name.Identifier.ValueText;
        var arity = name is GenericNameSyntax generic ? generic.Arity : 0;
        if (d.Id == "CS0103" && text is "Log" or "Logger")
            return PluginLogHint(compilation, text);
        string? ns = null;
        // Attributes: [HarmonyPatch] is looked up as HarmonyPatchAttribute too.
        foreach (var candidate in new[] { text, text.EndsWith("Attribute", StringComparison.Ordinal) ? text[..^"Attribute".Length] : null })
        {
            if (candidate is not null && (WellKnownTypes.TryGetValue($"{candidate}`{arity}", out ns) || WellKnownTypes.TryGetValue(candidate, out ns)))
            {
                text = candidate;
                break;
            }
        }
        if (ns is null)
            return null;

        // Unity modules and packages (the new Input System above all) are not in every game.
        if (ns.StartsWith("UnityEngine.", StringComparison.Ordinal)
            && compilation.GetTypeByMetadataName($"{ns}.{text}" + (arity > 0 ? $"`{arity}" : "")) is null
            && compilation.GetTypeByMetadataName($"{ns}.{text}Attribute") is null)
            return ns == "UnityEngine.InputSystem"
                ? $"'{text}' is in {ns} (the new Input System), which this game does not include. Use UnityEngine.Input instead, for example Input.GetKeyDown(KeyCode.F)."
                : $"'{text}' is in {ns}, which this game does not include.";
        return $"'{text}' is in the namespace {ns}: add 'using {ns};' at the top of the file.";
    }

    /// <summary>
    /// "Log" or "Logger" used outside the plugin class, typically in a Harmony patch class: the logger
    /// lives on the plugin class, so the patch has to name that class.
    /// </summary>
    private static string PluginLogHint(CSharpCompilation compilation, string text)
    {
        var plugin = compilation.GetSymbolsWithName(_ => true, SymbolFilter.Type).OfType<INamedTypeSymbol>().FirstOrDefault(DerivesFromPlugin);
        if (plugin is null)
            return $"'{text}' is not visible here. Keep the logger in a static field on your plugin class (internal static ManualLogSource Log; set Log = Logger; in Awake) and write YourPluginClass.Log.";
        var field = plugin.GetMembers().FirstOrDefault(m => m.IsStatic && m switch
        {
            IFieldSymbol f => f.Type.Name == "ManualLogSource",
            IPropertySymbol p => p.Type.Name == "ManualLogSource",
            _ => false,
        });
        return field is not null
            ? $"'{text}' is not visible in this class. The logger is a static member of your plugin class: write {plugin.Name}.{field.Name} here."
            : $"'{text}' is not visible in this class. Add 'internal static ManualLogSource Log;' to {plugin.Name}, set 'Log = Logger;' in its Awake, and write {plugin.Name}.Log here.";
    }

    private static bool DerivesFromPlugin(INamedTypeSymbol type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.ToDisplayString() == "BepInEx.BaseUnityPlugin")
                return true;
        return false;
    }

    /// <summary>
    /// For CS0122 on a game member: says what the member is and shows how to reach it
    /// (a ___name patch parameter or AccessTools). Null when the member can't be identified.
    /// </summary>
    public static string? PrivateMemberHint(CSharpCompilation compilation, Diagnostic d)
    {
        if (d.Id != "CS0122" || d.Location.SourceTree is not { } tree)
            return null;
        var node = tree.GetRoot().FindNode(d.Location.SourceSpan, getInnermostNodeForTie: true);
        var info = compilation.GetSemanticModel(tree).GetSymbolInfo(node);
        var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
        var owner = symbol?.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var access = symbol?.DeclaredAccessibility.ToString().ToLowerInvariant().Replace("protectedorinternal", "protected internal").Replace("protectedandinternal", "private protected");
        return symbol switch
        {
            IFieldSymbol f =>
                $"'{f.Name}' is a {access} field of {owner}, so it can't be used directly. In a Harmony patch of a {owner} method, add a parameter " +
                $"'ref {f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} ___{f.Name}' (three underscores); elsewhere use " +
                $"AccessTools.FieldRefAccess<{owner}, {f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>(\"{f.Name}\")(instance).",
            IMethodSymbol m =>
                $"'{m.Name}' is a {access} method of {owner}, so it can't be called directly. Use AccessTools.Method(typeof({owner}), \"{m.Name}\").Invoke(instance, new object[] {{ ... }}), " +
                $"or patch it with [HarmonyPatch(typeof({owner}), \"{m.Name}\")].",
            IPropertySymbol p =>
                $"'{p.Name}' is a {access} property of {owner}, so it can't be used directly. Use Traverse.Create(instance).Property(\"{p.Name}\").GetValue<{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>() " +
                $"or AccessTools.Property(typeof({owner}), \"{p.Name}\").",
            INamedTypeSymbol t =>
                $"'{t.Name}' is a {access} type, so it can't be named directly. Use AccessTools.TypeByName(\"{t.ToDisplayString()}\") and reach its members with AccessTools or Traverse.",
            _ => null,
        };
    }

    /// <summary>One-line advice for the compile errors a model most often makes.</summary>
    public static string? Hint(string id) => id switch
    {
        "CS0122" => "That member is private or protected: read it with HarmonyLib.AccessTools (AccessTools.Field(typeof(T), \"name\")) or Traverse.Create(obj).Field(\"name\").GetValue<T>(), or in a Harmony patch add a parameter named ___name.",
        "CS0246" or "CS0234" => "Unknown type: add the missing using (BepInEx, HarmonyLib, UnityEngine), or use search-code to find the game type and its namespace.",
        "CS0103" => "Unknown name: check the spelling, add the missing using, or use search-code to find where it is declared in the game.",
        "CS1061" or "CS0117" => "That type has no member with this name: use read-code on the type to see its real members.",
        "CS0619" or "CS0618" => "That API is obsolete in this game's Unity version: use the replacement the message names, or avoid it (for example keep objects in a HashSet instead of using their instance IDs).",
        "CS0115" => "There is no method with that signature to override: patch it with Harmony instead of overriding.",
        _ => null,
    };

    private static CompileError ToError(Diagnostic d) => ToError(d, null);

    private static CompileError ToError(Diagnostic d, string? specificHint)
    {
        var span = d.Location.GetLineSpan();
        var message = d.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        if (message.Length > 300)
            message = message[..300] + "...";
        return new CompileError(d.Id, span.Path ?? "", span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, message, specificHint ?? Hint(d.Id));
    }

    /// <summary>
    /// References for a game: BepInEx core first, then <c>Managed/*.dll</c>. Native dlls are skipped.
    /// When two files carry the same assembly name, the first one wins.
    /// <paramref name="bepInExCoreImages"/> stands in for <c>BepInEx/core</c> before BepInEx is on disk.
    /// </summary>
    public static List<MetadataReference> GameReferences(GameFacts facts, IEnumerable<(string Name, byte[] Image)>? bepInExCoreImages = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new List<MetadataReference>();

        void Add(byte[] image, string displayPath)
        {
            // BepInEx ships 0Harmony20.dll, a Harmony 2.0 compatibility copy with the same types as
            // 0Harmony.dll (HarmonyX); referencing both makes every Harmony type ambiguous.
            if (Path.GetFileName(displayPath).Equals("0Harmony20.dll", StringComparison.OrdinalIgnoreCase))
                return;
            var name = AssemblyName(image);
            if (name is not null && seen.Add(name))
                references.Add(MetadataReference.CreateFromImage(image, filePath: displayPath));
        }

        if (bepInExCoreImages is not null)
        {
            foreach (var (name, image) in bepInExCoreImages)
                Add(image, name);
        }
        else
        {
            var core = GameTree.Full(facts.FullPath, "BepInEx/core");
            if (Directory.Exists(core))
                foreach (var dll in Directory.GetFiles(core, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
                    Add(File.ReadAllBytes(dll), dll);
        }

        if (facts.ManagedDir is not null && Directory.Exists(facts.ManagedDir))
            foreach (var dll in Directory.GetFiles(facts.ManagedDir, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
                Add(File.ReadAllBytes(dll), dll);

        return references;
    }

    /// <summary>The assembly name from .NET metadata, or null for native or broken files.</summary>
    public static string? AssemblyName(byte[] image)
    {
        try
        {
            using var reader = new PEReader(new MemoryStream(image));
            if (!reader.HasMetadata)
                return null;
            var metadata = reader.GetMetadataReader();
            return metadata.IsAssembly ? metadata.GetString(metadata.GetAssemblyDefinition().Name) : null;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}
