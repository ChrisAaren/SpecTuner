namespace SpecTuner.Sidecar.Code;

/// <summary><c>decompiled/&lt;assemblyHash&gt;/index.json</c>: every type and member of one decompiled assembly.</summary>
public sealed class CodeIndex
{
    /// <summary>Bump when the cache layout or the decompiler output changes, so old caches are rebuilt.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Assembly { get; set; } = "";
    public string AssemblyFile { get; set; } = "";
    public string AssemblyHash { get; set; } = "";
    public double DecompileSeconds { get; set; }
    public List<TypeEntry> Types { get; set; } = [];
}

public sealed class TypeEntry
{
    /// <summary>C# style full name without generic arity, nested types joined with '.': <c>Game.Player.State</c>.</summary>
    public string FullName { get; set; } = "";
    /// <summary>Name as declared, with type parameters: <c>Pool&lt;T&gt;</c>.</summary>
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>Header up to the opening brace: <c>public class Player : MonoBehaviour</c>.</summary>
    public string Signature { get; set; } = "";
    /// <summary>File under <c>types/</c> holding the top-level type this one is in.</summary>
    public string File { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public List<MemberEntry> Members { get; set; } = [];
}

public sealed class MemberEntry
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Signature { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
}
