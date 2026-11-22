using System.Text.Json;
using Microsoft.CodeAnalysis;
using SpecTuner.Sidecar.Code;
using SpecTuner.Sidecar.Compilation;
using SpecTuner.Sidecar.State;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class CodeTests : SidecarTest
{
    /// <summary>A small "game" assembly: fields, properties, overloads, nested and generic types, a name clash, and one long type.</summary>
    private const string GameSource = """
        namespace Game
        {
            public class Player
            {
                private float jumpHeight = 2f;
                public int Health { get; set; }

                public void Jump()
                {
                    Jump(jumpHeight);
                }

                public void Jump(float height)
                {
                    Health -= (int)height;
                }

                public class State
                {
                    public int Value;
                }
            }

            public class Pool<T>
            {
                public T Get() { return default(T); }
            }

            public enum Mode { Easy, Hard }
        }

        namespace Other
        {
            public class Player
            {
                public void Run() { }
            }
        }

        namespace Game.Big
        {
            public class Huge
            {
        %HUGE%
            }
        }
        """;

    private static readonly Lazy<byte[]> GameAssembly = new(() =>
    {
        var huge = string.Join("\n", Enumerable.Range(0, 40).Select(i => $"        public int Method{i}(int x) {{ return x + {i}; }}"));
        var references = new[] { "System.Runtime.dll", "System.Private.CoreLib.dll" }
            .Select(f => (MetadataReference)MetadataReference.CreateFromFile(Path.Combine(UnityStub.RuntimeDirectory, f))).ToList();
        var result = PluginCompiler.Compile("Assembly-CSharp", [new SourceFile("Game.cs", GameSource.Replace("%HUGE%", huge))], references);
        Assert.IsTrue(result.Success, string.Join("; ", result.Errors.Select(e => e.Message)));
        return result.Assembly!;
    });

    private GameFixture CreateCodeGame()
    {
        var game = CreateGame();
        game.Write($"{game.DataRelative}/Managed/Assembly-CSharp.dll", GameAssembly.Value);
        return game;
    }

    private static JsonElement Search(GameFixture game, string query, string kind = "any", int limit = 20) =>
        Run("search-code", new { gamePath = game.Root, query, kind, limit });

    private static JsonElement Read(GameFixture game, object request) => Run("read-code", request);

    [TestMethod]
    public void OwnAssemblies_ExcludeEngineAndFramework()
    {
        foreach (var name in new[] { "UnityEngine.dll", "UnityEngine.CoreModule.dll", "Unity.Burst.dll", "System.dll", "System.Core.dll", "mscorlib.dll", "netstandard.dll", "Mono.Security.dll", "Newtonsoft.Json.dll", "0Harmony.dll", "BepInEx.dll" })
            Assert.IsTrue(GameAssemblies.IsEngineOrFramework(name), name);
        foreach (var name in new[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "PlayerSave.dll", "SystemShock.dll" })
            Assert.IsFalse(GameAssemblies.IsEngineOrFramework(name), name);
    }

    [TestMethod]
    public void NameSearch_FindsMembersAndTypes_ExactFirst()
    {
        var game = CreateCodeGame();

        var data = Search(game, "jump", kind: "name").GetProperty("data");

        var hits = data.GetProperty("hits").EnumerateArray().ToList();
        Assert.AreEqual("Jump", hits[0].GetProperty("member").GetString(), "exact name matches come first");
        Assert.AreEqual(2, hits.Count(h => h.GetProperty("member").GetString() == "Jump"), "both overloads");
        Assert.IsTrue(hits.Any(h => h.GetProperty("member").GetString() == "jumpHeight"));
        Assert.AreEqual("Game.Player", hits[0].GetProperty("type").GetString());
        Assert.AreEqual("Assembly-CSharp", hits[0].GetProperty("assembly").GetString());
        StringAssert.Contains(hits[0].GetProperty("signature").GetString()!, "public void Jump(");
        Assert.IsGreaterThan(0, hits[0].GetProperty("line").GetInt32());
        Assert.IsFalse(hits[0].TryGetProperty("snippet", out _), "name hits carry only the signature");
    }

    [TestMethod]
    public void TextSearch_FindsSourceLines_InTheirMember()
    {
        var game = CreateCodeGame();

        var hits = Search(game, "Health -=", kind: "text").GetProperty("data").GetProperty("hits");

        Assert.AreEqual(1, hits.GetArrayLength());
        Assert.AreEqual("Jump", hits[0].GetProperty("member").GetString());
        StringAssert.Contains(hits[0].GetProperty("snippet").GetString()!, "Health -=");
        StringAssert.Contains(hits[0].GetProperty("signature").GetString()!, "float height");
    }

    [TestMethod]
    public void Results_AreCapped_WithTotalAndTruncated()
    {
        var game = CreateCodeGame();

        var data = Search(game, "Method", limit: 5).GetProperty("data");

        Assert.AreEqual(5, data.GetProperty("hits").GetArrayLength());
        Assert.IsGreaterThanOrEqualTo(40, data.GetProperty("total").GetInt32());
        Assert.IsTrue(data.GetProperty("truncated").GetBoolean());
        AssertRefused(Run("search-code", new { gamePath = game.Root, query = "x", limit = 5 }), "BAD_REQUEST");
        AssertRefused(Run("search-code", new { gamePath = game.Root, query = "Method", limit = 51 }), "BAD_REQUEST");
    }

    [TestMethod]
    public void Snippets_AreShort()
    {
        Assert.IsLessThanOrEqualTo(CodeSearch.MaxSnippet, CodeSearch.Snip(new string('a', 500) + "needle" + new string('b', 500), "needle").Length);
        StringAssert.Contains(CodeSearch.Snip(new string('a', 500) + "needle" + new string('b', 500), "needle"), "needle");
    }

    [TestMethod]
    public void Cache_IsBuiltOnce_AndReused()
    {
        var game = CreateCodeGame();
        var first = Search(game, "Jump").GetProperty("data").GetProperty("assemblies")[0];
        Assert.IsFalse(first.GetProperty("fromCache").GetBoolean());
        var index = Directory.GetFiles(Path.Combine(GameState.For(game.Root).Dir, "decompiled"), "index.json", SearchOption.AllDirectories).Single();
        var written = File.GetLastWriteTimeUtc(index);
        Assert.IsGreaterThanOrEqualTo(4, Directory.GetFiles(Path.Combine(Path.GetDirectoryName(index)!, "types"), "*.cs").Length, "one file per top-level type");

        var second = Search(game, "Pool").GetProperty("data").GetProperty("assemblies")[0];

        Assert.IsTrue(second.GetProperty("fromCache").GetBoolean());
        Assert.AreEqual(written, File.GetLastWriteTimeUtc(index));
    }

    [TestMethod]
    public void AssemblyParameter_TargetsOneAssembly_EvenAnEngineOne()
    {
        var game = CreateCodeGame();
        AssertOk(Run("search-code", new { gamePath = game.Root, query = "SetResolution", assembly = "UnityEngine" }));
        var hits = Run("search-code", new { gamePath = game.Root, query = "SetResolution" }).GetProperty("data").GetProperty("total").GetInt32();
        Assert.AreEqual(0, hits, "engine assemblies are not searched by default");
        AssertRefused(Run("search-code", new { gamePath = game.Root, query = "Jump", assembly = "Nope" }), "ASSEMBLY_NOT_FOUND");
    }

    [TestMethod]
    public void ReadMember_ReturnsEveryOverload_WithLineNumbers()
    {
        var game = CreateCodeGame();

        var data = Read(game, new { gamePath = game.Root, type = "Game.Player", member = "Jump" }).GetProperty("data");

        Assert.AreEqual(2, data.GetProperty("members").GetArrayLength());
        var code = data.GetProperty("code").GetString()!;
        StringAssert.Contains(code, "Jump(jumpHeight);");
        StringAssert.Contains(code, "Health -= (int)height;");
        StringAssert.Matches(code.Split('\n')[0], new System.Text.RegularExpressions.Regex(@"^\d+: "));
    }

    [TestMethod]
    public void AmbiguousSimpleName_ReturnsCandidates()
    {
        var game = CreateCodeGame();

        var envelope = Read(game, new { gamePath = game.Root, type = "Player" });

        AssertRefused(envelope, "AMBIGUOUS_TYPE");
        var candidates = envelope.GetProperty("data").GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("type").GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "Game.Player", "Other.Player" }, candidates);
    }

    [TestMethod]
    [DataRow("Game.Player+State")]
    [DataRow("Game.Player.State")]
    [DataRow("State")]
    public void NestedTypes_CanBeRead(string name)
    {
        var game = CreateCodeGame();
        var data = Read(game, new { gamePath = game.Root, type = name }).GetProperty("data");
        Assert.AreEqual("Game.Player.State", data.GetProperty("type").GetString());
        StringAssert.Contains(data.GetProperty("code").GetString()!, "public int Value;");
    }

    [TestMethod]
    [DataRow("Pool`1")]
    [DataRow("Game.Pool<T>")]
    public void GenericTypes_CanBeRead(string name)
    {
        var game = CreateCodeGame();
        var data = Read(game, new { gamePath = game.Root, type = name }).GetProperty("data");
        Assert.AreEqual("Game.Pool", data.GetProperty("type").GetString());
        StringAssert.Contains(data.GetProperty("code").GetString()!, "public T Get()");
    }

    [TestMethod]
    public void LongType_GivesAnOutline_AndStartLinePages()
    {
        var game = CreateCodeGame();

        var outline = Read(game, new { gamePath = game.Root, type = "Huge", maxLines = 20 }).GetProperty("data");

        Assert.AreEqual(40, outline.GetProperty("members").GetArrayLength());
        StringAssert.Contains(outline.GetProperty("note").GetString()!, "member");
        Assert.IsFalse(outline.TryGetProperty("code", out _));

        var line = int.Parse(outline.GetProperty("members")[10].GetString()!.Split(':')[0]);
        var page = Read(game, new { gamePath = game.Root, type = "Huge", maxLines = 10, startLine = line }).GetProperty("data");
        Assert.AreEqual(line, page.GetProperty("from").GetInt32());
        StringAssert.Contains(page.GetProperty("code").GetString()!, "Method10(");
        Assert.IsTrue(page.GetProperty("truncated").GetBoolean());
    }

    [TestMethod]
    public void UnknownTypeOrMember_SuggestsTheNearest()
    {
        var game = CreateCodeGame();

        var type = Read(game, new { gamePath = game.Root, type = "Playr" });
        AssertRefused(type, "TYPE_NOT_FOUND");
        CollectionAssert.Contains(type.GetProperty("data").GetProperty("nearest").EnumerateArray().Select(n => n.GetString()).ToList(), "Game.Player");

        var member = Read(game, new { gamePath = game.Root, type = "Game.Player", member = "Jmup" });
        AssertRefused(member, "MEMBER_NOT_FOUND");
        Assert.AreEqual("Jump", member.GetProperty("data").GetProperty("nearest")[0].GetString());
    }
}
