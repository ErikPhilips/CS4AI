using ErikPhilips.Cs4Ai;
using Microsoft.CodeAnalysis;
using Xunit;

namespace ErikPhilips.Cs4Ai.Tests;

/// <summary>
/// Engine-level tests for <see cref="CSEngine.ExecuteAsync"/> — the write-through edit core. They
/// drive the engine against a real <see cref="SolutionHost"/> (so write-through lands on disk) but
/// without the daemon/dispatch, covering round-trip edits, atomic-per-command rejection, and the
/// inferred-usings echo. The host's <see cref="SolutionHost.CurrentSolution"/> is the live view —
/// <see cref="CSEngine.Staged"/> returns it.
/// </summary>
public class CSEngineTests
{
    private const string Source = """
        namespace Acme;

        public class Calc
        {
            public int Add(int a, int b) => a + b;
        }
        """;

    private static async Task<(CSEngine engine, SolutionHost host)> EngineAsync(FixtureSolution fx)
    {
        var host = new SolutionHost(fx.SlnxPath);
        await host.GetCommittedSolutionAsync(default); // load the live view (no full build — engine-direct)
        var session = new EditSession { Token = "sess_test.0" };
        var engine = new CSEngine(host, session, Cs4AiConfig.Preset("sa1201"));
        return (engine, host);
    }

    private static async Task<string> TypeTokenAsync(CSEngine engine, string fqn)
    {
        var (resolved, err) = await engine.ResolveAsync(fqn, default);
        Assert.Null(err);
        var type = resolved.Symbol as INamedTypeSymbol;
        Assert.NotNull(type);
        return engine.TypeTokenFor(type!);
    }

    private static TypeOperations Group(string? token, params Operation[] ops) =>
        new() { Token = token, Ops = ops };

    // ── update (body replace) round-trip ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_BodyReplace_Stages_AndTokenTicks()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var before = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(before, new Operation
            {
                Op = Ops.Update,
                Source = "Acme.Calc.Add(int,int)",
                Body = "public int Add(int a, int b) => a + b + 1;",
            })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.True(r.FileEdited);
        Assert.Contains("+ b + 1", fx.ReadSource("Calc.cs")); // wrote through to disk immediately
        var after = await TypeTokenAsync(engine, "Acme.Calc");
        Assert.NotEqual(before, after);                       // token ticked
    }

    [Fact]
    public async Task Update_StaleToken_Exit5_NothingStaged()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var r = await engine.ExecuteAsync(
            [Group("type_deadbeef", new Operation
            {
                Op = Ops.Update,
                Source = "Acme.Calc.Add(int,int)",
                Body = "public int Add(int a, int b) => a + b + 1;",
            })], default);

        Assert.Equal(Cs4AiResult.CodeStale, r.ExitCode);
        Assert.Contains("stale", r.Output ?? "");
        Assert.DoesNotContain("+ b + 1", fx.ReadSource("Calc.cs")); // rejected before write — disk untouched
    }

    // ── create (member + new type) ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_Member_AddsToType()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation
            {
                Op = Ops.Create,
                Destination = "Acme.Calc.Sub(int,int)",
                Body = "public int Sub(int a, int b) => a - b;",
            })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("Sub", r.Output ?? "");
        Assert.Contains("[create ", r.Output ?? ""); // frame header leads with the op
    }

    [Fact]
    public async Task Create_Member_SortedBeforeSibling_NotGluedOntoIt()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        // "Aaa" sorts before "Add", so the sa1201 sort inserts the new member AHEAD of an
        // existing one — the stress-test #6 shape (append-at-end was always fine).
        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation
            {
                Op = Ops.Create,
                Destination = "Acme.Calc.Aaa()",
                Body = "public int Aaa() => 0;",
            })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        var text = fx.ReadSource("Calc.cs");
        Assert.DoesNotContain("=> 0; public", text); // not glued on one line
        var aaaLine = text.Split('\n').Single(l => l.Contains("Aaa()"));
        Assert.DoesNotContain("Add(", aaaLine);      // each member on its own line
    }

    [Fact]
    public async Task Create_NewTopLevelType_LandsInProject()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        // No token: a brand-new top-level type has no prior view.
        var r = await engine.ExecuteAsync(
            [Group(null, new Operation
            {
                Op = Ops.Create,
                Destination = "Acme.Person",
                Body = "public class Person { public string Name { get; set; } = \"\"; }",
            })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("Person", r.Output ?? "");
        var (resolved, err) = await engine.ResolveAsync("Acme.Person", default);
        Assert.Null(err);
        Assert.NotNull(resolved.Symbol);
    }

    [Fact]
    public async Task Create_NewType_InfersAndEchoesUsings()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        // Regex is NOT in ImplicitUsings → inference should add System.Text.RegularExpressions
        // and the result must echo it (so the agent sees what was inferred).
        var r = await engine.ExecuteAsync(
            [Group(null, new Operation
            {
                Op = Ops.Create,
                Destination = "Acme.Matcher",
                Body = "public class Matcher { public Regex? Pattern { get; set; } }",
            })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("System.Text.RegularExpressions", r.Output ?? "");
    }

    // ── --set-attributes (whole replace) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithAttributes_AttachesThem()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation
            {
                Op = Ops.Create,
                Destination = "Acme.Calc.Tag",
                Body = "public int Tag { get; set; }",
                Attributes = "[Obsolete],[System.ComponentModel.Description(\"x\")]",
            })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("[Obsolete]", r.Output ?? "");
        Assert.Contains("Description", r.Output ?? "");
    }

    [Fact]
    public async Task Update_SetAttributes_ReplacesWholeSet()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation
            {
                Op = Ops.Update,
                Source = "Acme.Calc.Add(int,int)",
                Attributes = "[Obsolete]",
            })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("[Obsolete]", r.Output ?? "");
        Assert.Contains("Add", r.Output ?? ""); // member kept, body unchanged
    }

    [Fact]
    public async Task Update_SetAttributes_OnType_AttachesThem()
    {
        // Regression: `[Serializable]` and friends are TYPE attributes — update --set-attributes must
        // work on a type, not just a member (the old guard wrongly rejected all facets on a type).
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Update, Source = "Acme.Calc", Attributes = "[Serializable]" })],
            default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("[Serializable]", fx.ReadSource("Calc.cs")); // written through to disk
    }

    [Fact]
    public async Task Update_SetUsings_ReplacesFileUsings()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc"); // --set-usings addresses the type
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation
            {
                Op = Ops.Update,
                Source = "Acme.Calc",
                Usings = "System.Text, System.Linq",
            })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("usings replaced", r.Output ?? ""); // file-wide-effect note

        var doc = engine.Staged.Projects.SelectMany(p => p.Documents)
                        .First(d => d.FilePath!.EndsWith("Calc.cs", StringComparison.Ordinal));
        var text = (await doc.GetTextAsync()).ToString();
        Assert.Contains("using System.Text;", text);
        Assert.Contains("using System.Linq;", text);
    }

    [Fact]
    public async Task Update_SetNamespace_MovesType_AndAddsUsingToCallers()
    {
        using var fx = new FixtureSolution(
            "namespace Acme;\n\npublic class Calc { public int Add(int a, int b) => a + b; }");
        // A caller in another file references Calc by simple name (same namespace, no using today).
        File.WriteAllText(Path.Combine(fx.SrcDir, "Caller.cs"),
            "namespace Acme;\n\npublic class Caller { public int Use() => new Calc().Add(1, 2); }");

        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Update, Source = "Acme.Calc", Namespace = "Acme.Math" })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("Acme.Math", r.Output ?? "");

        // The caller's file gained `using Acme.Math;` so its `new Calc()` still resolves.
        var caller = engine.Staged.Projects.SelectMany(p => p.Documents)
                           .First(d => d.FilePath!.EndsWith("Caller.cs", StringComparison.Ordinal));
        Assert.Contains("using Acme.Math;", (await caller.GetTextAsync()).ToString());

        // The type now lives at the new FQN.
        var (moved, mErr) = await engine.ResolveAsync("Acme.Math.Calc", default);
        Assert.Null(mErr);
        Assert.NotNull(moved.Symbol);
    }

    [Fact]
    public async Task SelfHeal_SetNamespace_RemovesNowBrokenUsings()
    {
        // Calc is the ONLY type in Acme; a consumer in another namespace imports Acme.
        using var fx = new FixtureSolution(
            "namespace Acme;\n\npublic class Calc { public int V() => 1; }");
        File.WriteAllText(Path.Combine(fx.SrcDir, "Consumer.cs"),
            "using Acme;\n\nnamespace Other;\n\npublic class Consumer { public int U() => new Calc().V(); }");

        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        // Move Calc to an UNRELATED namespace → Acme stops existing → `using Acme;` breaks (CS0246).
        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Update, Source = "Acme.Calc", Namespace = "Foo" })], default);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);

        var consumer = engine.Staged.Projects.SelectMany(p => p.Documents)
                             .First(d => d.FilePath!.EndsWith("Consumer.cs", StringComparison.Ordinal));
        var text = (await consumer.GetTextAsync()).ToString();
        Assert.Contains("using Foo;", text);          // cascade added the new namespace
        Assert.DoesNotContain("using Acme;", text);   // self-heal silently removed the now-broken one
    }

    [Fact]
    public async Task Update_SetNamespace_RewritesFullyQualifiedReferences()
    {
        using var fx = new FixtureSolution(
            "namespace Acme;\n\npublic class Calc { public int V() => 1; }");
        // A consumer that uses Calc FULLY QUALIFIED (no using) — both a type position and a new-expr.
        File.WriteAllText(Path.Combine(fx.SrcDir, "FqConsumer.cs"),
            "namespace Other;\n\npublic class FqConsumer { public Acme.Calc Make() => new Acme.Calc(); }");

        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Update, Source = "Acme.Calc", Namespace = "Acme.Math" })], default);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("fully-qualified", r.Output ?? ""); // the note reports the rewrite count

        var fq = engine.Staged.Projects.SelectMany(p => p.Documents)
                       .First(d => d.FilePath!.EndsWith("FqConsumer.cs", StringComparison.Ordinal));
        var text = (await fq.GetTextAsync()).ToString();
        Assert.Contains("Acme.Math.Calc", text);  // qualifier rewritten to the new namespace
        Assert.DoesNotContain("new Acme.Calc(", text);
    }

    // ── delete + rename ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Member_Removes()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Delete, Source = "Acme.Calc.Add(int,int)" })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        var (resolved, _) = await engine.ResolveAsync("Acme.Calc.Add(int,int)", default);
        Assert.Null(resolved.Symbol);
    }

    [Fact]
    public async Task Rename_Member_Cascades()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Rename, Source = "Acme.Calc.Add(int,int)", Destination = "Plus" })],
            default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("Plus", r.Output ?? "");
        Assert.Contains("[rename ", r.Output ?? "");     // op in the frame header
        Assert.Contains("Add -> Plus", r.Output ?? "");  // delta body, not a full re-dump
    }

    [Fact]
    public async Task Rename_Cascade_ReportsRewrittenFiles()
    {
        using var fx = new FixtureSolution(Source);
        File.WriteAllText(fx.SourceFile("Caller.cs"), """
            namespace Acme;

            public class Caller
            {
                public int Twice() => new Calc().Add(1, 1) + new Calc().Add(2, 2);
            }
            """);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Rename, Source = "Acme.Calc.Add(int,int)", Destination = "Plus" })],
            default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains(".Plus(", fx.ReadSource("Caller.cs"));                    // cascade landed
        Assert.Contains("references-rewritten: 1 other file", r.Output ?? "");    // …and is visible
        Assert.Contains("Caller.cs", r.Output ?? "");
    }

    // ── address forms the skill promises: ctor, indexer, overload signatures ────────────────────

    private const string AddressSource = """
        namespace Acme;

        public class Repo
        {
            public Repo(string name) { }
            public Repo(int id) { }
            public string this[int i] => "";
            public void Save() { }
            public void Save(int retries) { }
        }
        """;

    [Fact]
    public async Task Resolve_Constructor_ByCSharpSpelling()
    {
        using var fx = new FixtureSolution(AddressSource, "Repo.cs");
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var (r, err) = await engine.ResolveAsync("Acme.Repo.Repo(string)", default);
        Assert.Null(err);
        var ctor = Assert.IsAssignableFrom<IMethodSymbol>(r.Symbol);
        Assert.Equal(MethodKind.Constructor, ctor.MethodKind);
        Assert.Equal(SpecialType.System_String, ctor.Parameters.Single().Type.SpecialType);

        // A bare type name still means the TYPE, never its ctor.
        var (t, terr) = await engine.ResolveAsync("Acme.Repo", default);
        Assert.Null(terr);
        Assert.IsAssignableFrom<INamedTypeSymbol>(t.Symbol);
    }

    [Fact]
    public async Task Resolve_Indexer_ByCSharpSpelling()
    {
        using var fx = new FixtureSolution(AddressSource, "Repo.cs");
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var (r, err) = await engine.ResolveAsync("Acme.Repo.this[int]", default);
        Assert.Null(err);
        var prop = Assert.IsAssignableFrom<IPropertySymbol>(r.Symbol);
        Assert.True(prop.IsIndexer);
    }

    [Fact]
    public async Task Resolve_OverloadSignature_Disambiguates()
    {
        using var fx = new FixtureSolution(AddressSource, "Repo.cs");
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var (one, e1) = await engine.ResolveAsync("Acme.Repo.Save(int)", default);
        Assert.Null(e1);
        Assert.Single(((IMethodSymbol)one.Symbol!).Parameters);

        var (none, e0) = await engine.ResolveAsync("Acme.Repo.Save()", default);
        Assert.Null(e0);
        Assert.Empty(((IMethodSymbol)none.Symbol!).Parameters);

        var (_, amb) = await engine.ResolveAsync("Acme.Repo.Save", default);
        Assert.NotNull(amb);
        Assert.Equal(Cs4AiResult.CodeAmbiguous, amb!.Value.ExitCode);
        Assert.Contains("Save(int", amb.Value.Error ?? amb.Value.Output ?? ""); // candidates carry signatures

        var (_, miss) = await engine.ResolveAsync("Acme.Repo.Save(string)", default);
        Assert.NotNull(miss);
        Assert.Equal(Cs4AiResult.CodeNotFound, miss!.Value.ExitCode);           // sig matched no overload
    }

    // ── --set-file (reconcile a type with its file) ──────────────────────────────────────────────

    [Fact]
    public async Task Update_SetFile_SingleTypeFile_RenamesWholeFile()
    {
        // Calc is alone in Calc.cs → rename the whole file (git mv when tracked; plain rename here).
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Update, Source = "Acme.Calc", File = "Renamed.cs" })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.False(File.Exists(fx.SourceFile("Calc.cs")));            // old file gone
        Assert.True(File.Exists(fx.SourceFile("Renamed.cs")));          // new file present
        Assert.Contains("class Calc", fx.ReadSource("Renamed.cs"));     // content intact
        Assert.Contains("Renamed.cs", r.Output ?? "");                  // move reflected in the frame
        Assert.Contains("note:", r.Output ?? "");                       // mechanic note (git mv / renamed)
        // Type still resolves at the same FQN (only its file changed).
        var (moved, mErr) = await engine.ResolveAsync("Acme.Calc", default);
        Assert.Null(mErr);
        Assert.NotNull(moved.Symbol);
    }

    [Fact]
    public async Task Update_SetFile_IntoSubfolder_Works()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Update, Source = "Acme.Calc", File = "Models/Calc.cs" })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.False(File.Exists(fx.SourceFile("Calc.cs")));
        Assert.True(File.Exists(Path.Combine(fx.SrcDir, "Models", "Calc.cs")));
    }

    [Fact]
    public async Task Rename_WithSetFile_RenamesTypeAndFile()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation
            {
                Op = Ops.Rename, Source = "Acme.Calc", Destination = "Wallet", File = "Wallet.cs",
            })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("Calc -> Wallet", r.Output ?? "");            // one frame reports both
        Assert.False(File.Exists(fx.SourceFile("Calc.cs")));
        Assert.True(File.Exists(fx.SourceFile("Wallet.cs")));
        Assert.Contains("class Wallet", fx.ReadSource("Wallet.cs"));  // type renamed in the moved file
        var (moved, mErr) = await engine.ResolveAsync("Acme.Wallet", default);
        Assert.Null(mErr);
        Assert.NotNull(moved.Symbol);
    }

    [Fact]
    public async Task Update_SetFile_MultiTypeFile_ExtractsType()
    {
        // Two types in one file → extract Calc; Helper stays behind.
        using var fx = new FixtureSolution(
            "namespace Acme;\n\npublic class Calc { public int Add(int a, int b) => a + b; }\n\n" +
            "public class Helper { public int Z() => 0; }");
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Update, Source = "Acme.Calc", File = "Calc2.cs" })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("extracted", r.Output ?? "");
        Assert.Contains("class Calc", fx.ReadSource("Calc2.cs"));       // moved type in the new file
        Assert.DoesNotContain("class Helper", fx.ReadSource("Calc2.cs"));
        Assert.True(File.Exists(fx.SourceFile("Calc.cs")));            // old file still there
        Assert.Contains("class Helper", fx.ReadSource("Calc.cs"));     // sibling kept
        Assert.DoesNotContain("class Calc ", fx.ReadSource("Calc.cs"));// Calc removed from it
    }

    [Fact]
    public async Task Update_SetFile_IntoOccupiedFile_CoLocates()
    {
        // Calc is alone in Calc.cs; Other.cs (same namespace) already holds a type → co-locate Calc
        // into Other.cs and drop the now-empty Calc.cs. An occupied target is NOT a collision.
        using var fx = new FixtureSolution(Source);
        File.WriteAllText(fx.SourceFile("Other.cs"), "namespace Acme;\n\npublic class Other { }");
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Update, Source = "Acme.Calc", File = "Other.cs" })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("co-located into", r.Output ?? "");
        Assert.Contains("class Calc", fx.ReadSource("Other.cs"));      // moved in
        Assert.Contains("class Other", fx.ReadSource("Other.cs"));     // sibling kept
        Assert.False(File.Exists(fx.SourceFile("Calc.cs")));          // source was alone → removed
    }

    [Fact]
    public async Task Update_SetFile_IntoOccupiedFile_NamespaceMismatch_Rejected()
    {
        using var fx = new FixtureSolution(Source);
        File.WriteAllText(fx.SourceFile("Other.cs"), "namespace Acme.Sub;\n\npublic class Other { }");
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Update, Source = "Acme.Calc", File = "Other.cs" })], default);

        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
        Assert.Contains("namespaces must match", r.Output ?? r.Error ?? "");
        Assert.True(File.Exists(fx.SourceFile("Calc.cs")));           // untouched
    }

    // ── create --in-file: honor the file placement (co-locate / name / never silently ignore) ─────

    [Fact]
    public async Task Create_InFile_ExistingFile_CoLocates()
    {
        // The reported bug: create --in-file "Calc.cs" must place the new type INTO Calc.cs, not
        // silently create a separate Helper.cs.
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var r = await engine.ExecuteAsync(
            [Group(null, new Operation
            {
                Op = Ops.Create, Destination = "Acme.Helper",
                Body = "public class Helper { public int Z() => 0; }", InFile = "Calc.cs",
            })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("class Calc", fx.ReadSource("Calc.cs"));       // original kept
        Assert.Contains("class Helper", fx.ReadSource("Calc.cs"));     // co-located in the same file
        Assert.False(File.Exists(fx.SourceFile("Helper.cs")));        // NOT a separate file
    }

    [Fact]
    public async Task Create_InFile_NamespaceMismatch_Rejected()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        // Calc.cs is namespace Acme; the new type is Acme.Deep.* → mismatch, must reject.
        var r = await engine.ExecuteAsync(
            [Group(null, new Operation
            {
                Op = Ops.Create, Destination = "Acme.Deep.Helper",
                Body = "public class Helper {}", InFile = "Calc.cs",
            })], default);

        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
        Assert.Contains("namespaces must match", r.Output ?? r.Error ?? "");
        Assert.DoesNotContain("Helper", fx.ReadSource("Calc.cs"));    // nothing written
    }

    [Fact]
    public async Task Create_InFile_NoMatch_NamesNewFile()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var r = await engine.ExecuteAsync(
            [Group(null, new Operation
            {
                Op = Ops.Create, Destination = "Acme.Helper",
                Body = "public class Helper {}", InFile = "Brand.cs",
            })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.True(File.Exists(fx.SourceFile("Brand.cs")));          // named by --in-file
        Assert.False(File.Exists(fx.SourceFile("Helper.cs")));       // not <leaf>.cs
        Assert.Contains("class Helper", fx.ReadSource("Brand.cs"));
    }

    // ── create default-file collision (the Result / Result<T> clobber) ────────────────────────────

    [Fact]
    public async Task Create_DefaultFileCollision_CoLocates_NoClobber()
    {
        // The reported bug: create Result, then create Result<T> — both default to Result.cs. The
        // second AddDocument at the same path clobbered the first type on disk while the in-memory
        // view kept both, so `build` reported a false green. Must co-locate instead.
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var c1 = await engine.ExecuteAsync(
            [Group(null, new Operation
            {
                Op = Ops.Create, Destination = "Acme.Wrap",
                Body = "public class Wrap { public int Kind { get; set; } }",
            })], default);
        Assert.Equal(Cs4AiResult.CodeOk, c1.ExitCode);

        var c2 = await engine.ExecuteAsync(
            [Group(null, new Operation
            {
                Op = Ops.Create, Destination = "Acme.Wrap<T>",
                Body = "public sealed class Wrap<T> : Wrap { public T? Value { get; set; } }",
            })], default);
        Assert.Equal(Cs4AiResult.CodeOk, c2.ExitCode);

        var onDisk = fx.ReadSource("Wrap.cs");
        Assert.Contains("public class Wrap", onDisk);          // first type survived on disk
        Assert.Contains("class Wrap<T>", onDisk);              // second co-located beside it
        var (nonGeneric, e1) = await engine.ResolveAsync("Acme.Wrap", default);
        var (generic, e2) = await engine.ResolveAsync("Acme.Wrap<T>", default);
        Assert.Null(e1);
        Assert.Null(e2);
        Assert.NotNull(nonGeneric.Symbol);                     // view matches disk — no false green
        Assert.NotNull(generic.Symbol);
    }

    [Fact]
    public async Task Create_SameTypeTwice_Rejected()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        // Same name AND same arity in the same file is a duplicate, not a co-location.
        var r = await engine.ExecuteAsync(
            [Group(null, new Operation
            {
                Op = Ops.Create, Destination = "Acme.Calc",
                Body = "public class Calc { }",
            })], default);

        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
        Assert.Contains("already declares", r.Output ?? r.Error ?? "");
        Assert.Contains("Add(int a, int b)", fx.ReadSource("Calc.cs")); // original untouched
    }

    [Fact]
    public async Task Create_FileOnDiskButNotInProject_Rejected()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        // On disk but not in the loaded workspace (written after load): never silently overwrite.
        File.WriteAllText(fx.SourceFile("Stray.cs"), "// not part of the loaded project\n");
        var r = await engine.ExecuteAsync(
            [Group(null, new Operation
            {
                Op = Ops.Create, Destination = "Acme.Stray",
                Body = "public class Stray { }",
            })], default);

        Assert.Equal(Cs4AiResult.CodeFileOrParse, r.ExitCode);
        Assert.Contains("refusing to overwrite", r.Output ?? r.Error ?? "");
        Assert.Contains("not part of the loaded project", fx.ReadSource("Stray.cs")); // untouched
    }

    [Fact]
    public async Task CoLocate_ThenSetFile_RoundTrips()
    {
        // The harness scenario: create Transaction, co-locate Money into it, then split Money back out.
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var c1 = await engine.ExecuteAsync(
            [Group(null, new Operation
            {
                Op = Ops.Create, Destination = "Acme.Transaction", Body = "public class Transaction {}",
            })], default);
        Assert.Equal(Cs4AiResult.CodeOk, c1.ExitCode);

        var c2 = await engine.ExecuteAsync(
            [Group(null, new Operation
            {
                Op = Ops.Create, Destination = "Acme.Money", Body = "public class Money {}", InFile = "Transaction.cs",
            })], default);
        Assert.Equal(Cs4AiResult.CodeOk, c2.ExitCode);
        Assert.Contains("class Money", fx.ReadSource("Transaction.cs"));   // co-located

        var token = await TypeTokenAsync(engine, "Acme.Money");
        var c3 = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Update, Source = "Acme.Money", File = "Money.cs" })], default);
        Assert.Equal(Cs4AiResult.CodeOk, c3.ExitCode);
        Assert.Contains("extracted", c3.Output ?? "");
        Assert.Contains("class Money", fx.ReadSource("Money.cs"));         // split back out
        Assert.DoesNotContain("class Money", fx.ReadSource("Transaction.cs"));
        Assert.Contains("class Transaction", fx.ReadSource("Transaction.cs"));
    }

    [Fact]
    public async Task Update_SetFile_EscapesProject_Rejected()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Update, Source = "Acme.Calc", File = "../Escape.cs" })], default);

        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
        Assert.Contains("escapes the project", r.Output ?? r.Error ?? "");
    }

    [Fact]
    public async Task Update_SetFile_OnMember_Rejected()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation
            {
                Op = Ops.Update, Source = "Acme.Calc.Add(int,int)", File = "Add.cs",
            })], default);

        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
        Assert.Contains("addresses a type", r.Output ?? r.Error ?? "");
    }

    [Fact]
    public async Task Rename_NoSetFile_SingleTypeDrift_EmitsReconcileHint()
    {
        // Renaming Calc (alone in Calc.cs) to Wallet leaves the file name stale → nudge (don't act).
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Rename, Source = "Acme.Calc", Destination = "Wallet" })], default);

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("reconcile with --set-file", r.Output ?? "");
        Assert.Contains("Wallet.cs", r.Output ?? "");
        Assert.True(File.Exists(fx.SourceFile("Calc.cs")));           // hint only — file NOT moved
    }

    // ── all-or-nothing across groups ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Batch_OneStaleGroup_RejectsWholeBatch()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var goodToken = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
        [
            Group(goodToken, new Operation
            {
                Op = Ops.Update, Source = "Acme.Calc.Add(int,int)",
                Body = "public int Add(int a, int b) => a + b + 1;",
            }),
            Group("type_stale", new Operation { Op = Ops.Delete, Source = "Acme.Calc.Add(int,int)" }),
        ], default);

        Assert.Equal(Cs4AiResult.CodeStale, r.ExitCode);
        // Atomic per command: the stale group rejects the whole batch BEFORE any write — disk untouched.
        Assert.Contains("public int Add(int a, int b) => a + b;", fx.ReadSource("Calc.cs"));
        Assert.DoesNotContain("+ b + 1", fx.ReadSource("Calc.cs"));
    }

    // ── grammar still gates at the engine boundary (forbidden field) ─────────────────────────────

    [Fact]
    public async Task Delete_WithBody_Exit1_AtEngine()
    {
        using var fx = new FixtureSolution(Source);
        var (engine, host) = await EngineAsync(fx);
        await using var _h = host;

        var token = await TypeTokenAsync(engine, "Acme.Calc");
        var r = await engine.ExecuteAsync(
            [Group(token, new Operation { Op = Ops.Delete, Source = "Acme.Calc.Add(int,int)", Body = "x" })], default);

        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
    }
}
