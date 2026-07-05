using System.Text.RegularExpressions;
using ErikPhilips.Cs4Ai;
using Xunit;

namespace ErikPhilips.Cs4Ai.Tests;

/// <summary>
/// End-to-end verb tests on the work-span surface, driven through the in-process engine (no daemon).
/// The pivot: <c>session</c> opens a work-span (full build at open), edits <b>write through to disk
/// immediately</b> (no commit), and <c>verify</c> is the end-of-work report. They exercise the live
/// dispatch path and assert directly on disk.
/// </summary>
public class VerbTests
{
    private const string Source = """
        namespace Acme;

        public class Calc
        {
            public int Add(int a, int b) => a + b;
        }
        """;

    private static Cs4AiEngine M => new(inProcess: true);

    // The result is plain framed text (no JSON). The session line is:
    //   session opened|created · <path> · sess_<key>.<rand>
    private static string SessionToken(string output) =>
        Regex.Match(output, @"(sess_[0-9a-fA-F]+\.[0-9a-fA-F]+)").Groups[1].Value;

    private static string SessionPath(string output) =>
        Regex.Match(output, @"session (?:opened|created) · (.+?) · sess_").Groups[1].Value;

    // The body must be framed text, not a JSON envelope. (Can't just check for '{' — echoed C# code
    // and .sln GUIDs contain braces; the JSON envelopes started with '{' and carried "result":.)
    private static void AssertNotJson(string output)
    {
        Assert.False(output.TrimStart().StartsWith('{'), "result must be framed text, not a JSON envelope");
        Assert.DoesNotContain("\"result\":", output);
    }

    private static string? TypeTokenFromInspect(string output)
    {
        var m = Regex.Match(output, @"(type_[0-9a-fA-F]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static async Task<(Cs4AiEngine m, string sess)> OpenAsync(FixtureSolution fx)
    {
        var m = M;
        var r = await m.ExecuteAsync(["session", fx.SlnxPath]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        return (m, SessionToken(r.Output!));
    }

    // ── session ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Session_ExistingSolution_ReturnsToken_Opened_WithOpeningBuildOutcome()
    {
        using var fx = new FixtureSolution(Source);
        await using var m = M;
        var r = await m.ExecuteAsync(["session", fx.SlnxPath]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        AssertNotJson(r.Output!);                 // framed text, not JSON
        Assert.Contains("session opened", r.Output!);
        Assert.StartsWith("sess_", SessionToken(r.Output!));
        Assert.Contains("[build passed", r.Output!);           // opening floor: clean fixture
    }

    [Fact]
    public async Task Session_NewSolutionPath_CreatesReportsPath_GuardsToCreateProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs4ai-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var m = M;
            // An absent solution-file path → cs4ai creates the dir + the solution.
            var s = await m.ExecuteAsync(["session", Path.Combine(dir, "Hello.slnx")]);
            Assert.Equal(Cs4AiResult.CodeOk, s.ExitCode);
            AssertNotJson(s.Output!);
            Assert.Contains("session created", s.Output!);

            var path = SessionPath(s.Output!);
            Assert.False(string.IsNullOrEmpty(path));
            Assert.True(File.Exists(path), "reported solution path exists on disk");
            Assert.True(File.Exists(Path.Combine(dir, ".cs4aiconfig")), "default config seeded");

            // Empty solution: an edit verb is refused, pointing at create-project.
            var sess = SessionToken(s.Output!);
            var edit = await m.ExecuteAsync(["create", sess, "Acme.Foo", "--set-body", "public class Foo {}"]);
            Assert.Equal(Cs4AiResult.CodeUsage, edit.ExitCode);
            Assert.Contains("create-project", edit.Error ?? edit.Output ?? "");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Session_Directory_OpensTheSolutionInIt()
    {
        using var fx = new FixtureSolution(Source);
        await using var m = M;
        var r = await m.ExecuteAsync(["session", fx.Root]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("session opened", r.Output!);
    }

    [Fact]
    public async Task Session_Create_DefaultsToSlnx()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs4ai-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var m = M;
            var r = await m.ExecuteAsync(["session", dir]);
            Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
            Assert.Contains("session created", r.Output!);
            Assert.True(File.Exists(Path.Combine(dir, new DirectoryInfo(dir).Name + ".slnx")), ".slnx is the default");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Session_Create_FormatSln_OptsOut()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs4ai-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var m = M;
            var r = await m.ExecuteAsync(["session", dir, "--format", "sln"]);
            Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
            var name = new DirectoryInfo(dir).Name;
            Assert.True(File.Exists(Path.Combine(dir, name + ".sln")));
            Assert.False(File.Exists(Path.Combine(dir, name + ".slnx")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Session_Create_ExplicitExtension_Wins()
    {
        // Latent-bug regression: `session foo.sln` used to create whatever `dotnet new sln`
        // emitted, silently ignoring the promised extension.
        var dir = Path.Combine(Path.GetTempPath(), "cs4ai-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var m = M;
            var r = await m.ExecuteAsync(["session", Path.Combine(dir, "Exact.sln")]);
            Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
            Assert.True(File.Exists(Path.Combine(dir, "Exact.sln")));
            Assert.False(File.Exists(Path.Combine(dir, "Exact.slnx")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Session_Create_FormatConflict_Exit1_NothingCreated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs4ai-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var m = M;
            var r = await m.ExecuteAsync(["session", Path.Combine(dir, "Foo.sln"), "--format", "slnx"]);
            Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
            Assert.False(File.Exists(Path.Combine(dir, "Foo.sln")));
            Assert.False(File.Exists(Path.Combine(dir, "Foo.slnx")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task FromEmpty_CreateProject_CreateType_WritesThrough_ReloadSeam()
    {
        // Empty dir → session → create-project (dotnet new + full build + reload) → create a type that
        // lands in the new project and is on disk IMMEDIATELY (no commit). Proves the graph-edit reload
        // rebuilt the workspace so the create resolves the new project. Shells dotnet — slower.
        var dir = Path.Combine(Path.GetTempPath(), "cs4ai-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var m = M;
            var sess = SessionToken((await m.ExecuteAsync(["session", Path.Combine(dir, "Acme.slnx")])).Output!);

            var cp = await m.ExecuteAsync(["create-project", sess, "Acme.Lib", "--path", "Acme.Lib"]);
            Assert.Equal(Cs4AiResult.CodeOk, cp.ExitCode);

            var create = await m.ExecuteAsync(
                ["create", sess, "Acme.Lib.Person",
                 "--set-body", "public class Person { public string Name { get; set; } = \"\"; }"]);
            Assert.Equal(Cs4AiResult.CodeOk, create.ExitCode);

            // Write-through: the file is on disk the moment the edit succeeds — no commit.
            Assert.True(File.Exists(Path.Combine(dir, "Acme.Lib", "Person.cs")), "Person.cs on disk");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AddReference_ThatBreaksWorkspaceLoad_Reports_NotExit2_AndDeleteRecovers()
    {
        // Regression: on net10, adding System.Text.Json (in the shared framework) trips NU1510, which
        // the SDK can escalate to a fatal MSBuild load error. The graph edit must REPORT (not abort
        // with exit 2 when the Roslyn reload throws), and delete-reference must recover.
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var add = await m.ExecuteAsync(["add-reference", sess, "src/Fixture.csproj", "System.Text.Json"]);
        Assert.NotEqual(Cs4AiResult.CodeFileOrParse, add.ExitCode); // the old bug was exit 2 here
        Assert.Equal(Cs4AiResult.CodeOk, add.ExitCode);             // report, not abort
        // Structure ≈ semantic: framed text (no JSON), an [add-reference …] frame + a [build …] block.
        AssertNotJson(add.Output!);
        Assert.Contains("[add-reference ", add.Output!);
        Assert.Contains("[build ", add.Output!);

        // A CODE EDIT must still work while the NU1510 reference is present — the command is valid, so
        // exit 0, even though the workspace carries a build-layer problem. (This was the user's case.)
        var create = await m.ExecuteAsync(
            ["create", sess, "Acme.Account", "--set-body", "public class Account { public decimal Balance { get; set; } }"]);
        Assert.NotEqual(Cs4AiResult.CodeFileOrParse, create.ExitCode);
        Assert.Equal(Cs4AiResult.CodeOk, create.ExitCode);

        var del = await m.ExecuteAsync(["delete-reference", sess, "src/Fixture.csproj", "System.Text.Json"]);
        Assert.Equal(Cs4AiResult.CodeOk, del.ExitCode);            // recovery reloads cleanly
    }

    // ── reads ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inspect_ViaSession_ReturnsFramedTypeWithToken()
    {
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var r = await m.ExecuteAsync(["inspect", sess, "Acme.Calc"]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("Acme.Calc", r.Output!);
        Assert.Contains("lines · type_", r.Output!);
        Assert.Contains("int Add", r.Output!);
    }

    // Bug #5: a bare name matching two types in different namespaces.
    private const string AmbiguousSource =
        "namespace Acme { public class Widget { } }\nnamespace Acme.Sub { public class Widget { } }";

    [Fact]
    public async Task Inspect_AmbiguousBareName_ShowsAllWithNote()
    {
        using var fx = new FixtureSolution(AmbiguousSource);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var r = await m.ExecuteAsync(["inspect", sess, "Widget"]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);        // multi-hit read is a valid answer
        Assert.Contains("ambiguous 'Widget'", r.Output!);    // …but the ambiguity is signalled
        Assert.Contains("Acme.Widget", r.Output!);           // both types shown
        Assert.Contains("Acme.Sub.Widget", r.Output!);
    }

    [Fact]
    public async Task Rename_AmbiguousBareName_Exit3_NoMutation()   // the risky variant stays safe
    {
        using var fx = new FixtureSolution(AmbiguousSource);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var r = await m.ExecuteAsync(["rename", sess, "Widget", "Renamed", "--token", "type_bogus0000"]);
        Assert.Equal(Cs4AiResult.CodeAmbiguous, r.ExitCode);       // exit 3 before any write
        Assert.Contains("ambiguous", (r.Output ?? "") + (r.Error ?? ""));
        Assert.Contains("class Widget", fx.ReadSource("Calc.cs")); // untouched
    }

    [Fact]
    public async Task Discover_ViaSession_ShowsCensus()
    {
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var r = await m.ExecuteAsync(["discover", sess, "Calc"]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("Types: [", r.Output!);
    }

    [Fact]
    public async Task Discover_TwoCallsOneLine_CollapsedWithCount()
    {
        // Two occurrences on one physical line used to render as duplicate entries — looked like a
        // glitch (issue #5). One entry per line, ×N for multiples, header count stays per-occurrence.
        const string src = """
            namespace Acme;

            public class Money
            {
                public static int From(int x) => x;
            }

            public class Uses
            {
                public int A() => Money.From(1) + Money.From(2);
                public int B() => Money.From(3);
            }
            """;
        using var fx = new FixtureSolution(src);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var r = await m.ExecuteAsync(["discover", sess, "From"]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        var output = r.Output!;
        Assert.Contains("Referenced by: [3 refs · 2 sites]", output);     // header pre-answers the ×2
        Assert.Contains("Calc.cs:10 ×2", output);                         // collapsed, annotated
        Assert.Contains("Calc.cs:11", output);                            // single stays bare
        Assert.Equal(1, output.Split("Calc.cs:10").Length - 1);           // :10 appears exactly once
    }

    // ── write-through edits ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateMember_WritesThroughImmediately_NoCommit()
    {
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var inspect = await m.ExecuteAsync(["inspect", sess, "Acme.Calc"]);
        var token = TypeTokenFromInspect(inspect.Output!);
        Assert.NotNull(token);

        var create = await m.ExecuteAsync(
            ["create", sess, "Acme.Calc.Sub(int,int)", "--token", token!,
             "--set-body", "public int Sub(int a, int b) => a - b;"]);
        Assert.Equal(Cs4AiResult.CodeOk, create.ExitCode);
        Assert.True(create.FileEdited);

        // No commit — the member is on disk the moment the edit succeeded.
        Assert.Contains("Sub", fx.ReadSource("Calc.cs"));
    }

    [Fact]
    public async Task EditBreaksBuild_Exit0_FailedBlock_NewCsError()
    {
        // The core ask: the operation succeeded (exit 0, written through) but the code doesn't compile
        // — the build axis says so. Two independent axes.
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var token = TypeTokenFromInspect((await m.ExecuteAsync(["inspect", sess, "Acme.Calc"])).Output!);
        var r = await m.ExecuteAsync(
            ["create", sess, "Acme.Calc.Bad()", "--token", token!,
             "--set-body", "public Zzz Bad() => default!;"]); // Zzz is unknown → CS0246

        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);       // the op happened
        Assert.Contains("[build failed", r.Output!);        // …but the build axis reports the breakage
        Assert.Contains("CS0246", r.Output!);
        Assert.Contains("Bad", fx.ReadSource("Calc.cs"));   // and it's on disk — git owns the undo
    }

    [Fact]
    public async Task Update_StaleToken_ReturnsExit5_DiskUntouched()
    {
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var r = await m.ExecuteAsync(
            ["update", sess, "Acme.Calc.Add(int,int)", "--token", "type_deadbeef",
             "--set-body", "public int Add(int a, int b) => a + b + 1;"]);
        Assert.Equal(Cs4AiResult.CodeStale, r.ExitCode);
        Assert.Contains("stale", r.Output!);
        Assert.DoesNotContain("+ b + 1", fx.ReadSource("Calc.cs")); // rejected before write
    }

    [Fact]
    public async Task ExternalDrift_StalesTheToken_Exit5()
    {
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var token = TypeTokenFromInspect((await m.ExecuteAsync(["inspect", sess, "Acme.Calc"])).Output!);

        // An external process rewrites Calc.cs (adds a member) — the warm image is now behind disk.
        File.WriteAllText(fx.SourceFile("Calc.cs"),
            "namespace Acme;\n\npublic class Calc { public int Add(int a, int b) => a + b; public int Mul(int a, int b) => a * b; }");

        // Editing with the token from before the external change → drift-reload changed the type's
        // token → exit 5 (re-inspect reality).
        var r = await m.ExecuteAsync(
            ["update", sess, "Acme.Calc.Add(int,int)", "--token", token!,
             "--set-body", "public int Add(int a, int b) => a + b + 1;"]);
        Assert.Equal(Cs4AiResult.CodeStale, r.ExitCode);
    }

    [Fact]
    public async Task EditVerb_UnknownSession_ReturnsExit7()
    {
        using var fx = new FixtureSolution(Source);
        await using var m = M;
        var open = await m.ExecuteAsync(["session", fx.SlnxPath]);
        var realSess = SessionToken(open.Output!);
        var pipeKey = realSess[5..realSess.IndexOf('.')];
        var bogus = $"sess_{pipeKey}.0000000000000000";

        var r = await m.ExecuteAsync(
            ["update", bogus, "Acme.Calc.Add(int,int)", "--token", "type_x",
             "--set-body", "public int Add(int a, int b) => 0;"]);
        Assert.Equal(Cs4AiResult.CodeNoSession, r.ExitCode);
    }

    [Fact]
    public async Task Create_UnknownFlag_ReturnsExit1()
    {
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var r = await m.ExecuteAsync(
            ["create", sess, "Acme.Thing", "--set-body", "public class Thing {}", "--bogus", "x"]);
        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
    }

    // ── verify ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Verify_IsReportNotGate_ReturnsFullOutcome()
    {
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _m = m;

        var r = await m.ExecuteAsync(["verify", sess]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode); // report, never refuses
        AssertNotJson(r.Output!);        // framed text, not JSON
        Assert.StartsWith("verify", r.Output!);
        Assert.Contains("[build ", r.Output!);
        Assert.DoesNotContain("[raw dotnet output]", r.Output!); // raw is opt-in
    }

    [Fact]
    public async Task Verify_Raw_AppendsVerbatimTranscript()
    {
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _m = m;

        var r = await m.ExecuteAsync(["verify", sess, "--raw"]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.Contains("[raw dotnet output]", r.Output!);
        Assert.Contains("── dotnet build ──", r.Output!); // the verbatim transcript, fenced
    }

    // ── config gate ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inspect_NoConfig_ReturnsExit6()
    {
        using var fx = new FixtureSolution(Source);
        File.Delete(Cs4AiConfig.PathFor(fx.Root));

        await using var m = M;
        var open = await m.ExecuteAsync(["session", fx.SlnxPath]); // session isn't config-gated
        Assert.Equal(Cs4AiResult.CodeOk, open.ExitCode);
        var sess = SessionToken(open.Output!);

        var r = await m.ExecuteAsync(["inspect", sess, "Acme.Calc"]);
        Assert.Equal(Cs4AiResult.CodeNeedsConfig, r.ExitCode);
    }

    [Fact]
    public async Task Init_WritesConfig()
    {
        using var fx = new FixtureSolution(Source);
        File.Delete(Cs4AiConfig.PathFor(fx.Root));

        await using var m = M;
        var r = await m.ExecuteAsync(["init", fx.SlnxPath, "sa1201"]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.True(File.Exists(Cs4AiConfig.PathFor(fx.Root)));
    }

    // ── --log: live per-session command journal ────────────────────────────────────────────────────

    private static string[] LogFiles(FixtureSolution fx) => Directory.GetFiles(fx.Root, "cs4ai_*.log");

    [Fact]
    public async Task Session_Log_IsLiveJournal_WrittenPerCommand()
    {
        using var fx = new FixtureSolution(Source);
        await using var m = M;

        var open = await m.ExecuteAsync(["session", fx.SlnxPath, "--log"]);
        Assert.Equal(Cs4AiResult.CodeOk, open.ExitCode);
        var sess = SessionToken(open.Output!);

        // Live journal: the file exists after the very first command (no verify needed) — a hung
        // command would leave a "#N started" entry rather than vanishing.
        var log = Assert.Single(LogFiles(fx));
        var afterOpen = File.ReadAllText(log);
        Assert.Contains("#1 exit code: 0", afterOpen);
        Assert.Contains("cs4ai session", afterOpen);

        await m.ExecuteAsync(["inspect", sess, "Acme.Calc"]);
        Assert.Contains($"cs4ai inspect {sess} Acme.Calc", File.ReadAllText(log)); // updated in place
    }

    [Fact]
    public async Task Session_Log_WritesTranscriptOnVerify()
    {
        using var fx = new FixtureSolution(Source);
        await using var m = M;

        var open = await m.ExecuteAsync(["session", fx.SlnxPath, "--log"]);
        Assert.Equal(Cs4AiResult.CodeOk, open.ExitCode);
        var sess = SessionToken(open.Output!);

        var inspect = await m.ExecuteAsync(["inspect", sess, "Acme.Calc"]);
        var token = TypeTokenFromInspect(inspect.Output!);
        var create = await m.ExecuteAsync(
            ["create", sess, "Acme.Calc.Sub(int,int)", "--token", token!,
             "--set-body", "public int Sub(int a, int b) => a - b;"]);
        Assert.Equal(Cs4AiResult.CodeOk, create.ExitCode);

        var verify = await m.ExecuteAsync(["verify", sess]);
        Assert.Equal(Cs4AiResult.CodeOk, verify.ExitCode);

        var log = Assert.Single(LogFiles(fx));
        Assert.EndsWith($"cs4ai_{sess}.log", log);
        var text = File.ReadAllText(log);

        Assert.Contains("#1 exit code: 0", text);
        Assert.Contains("#4 exit code: 0", text);
        Assert.Contains("cs4ai session", text);
        Assert.Contains("--log", text);
        Assert.Contains($"cs4ai inspect {sess} Acme.Calc", text);
        Assert.Contains($"cs4ai create {sess} Acme.Calc.Sub(int,int)", text);
        Assert.Contains($"cs4ai verify {sess}", text); // verify logs itself, in its own file
    }

    [Fact]
    public async Task Session_Log_StaleCommandRecordedWithExitCode()
    {
        using var fx = new FixtureSolution(Source);
        await using var m = M;

        var sess = SessionToken((await m.ExecuteAsync(["session", fx.SlnxPath, "--log"])).Output!);

        var stale = await m.ExecuteAsync(
            ["update", sess, "Acme.Calc.Add(int,int)", "--token", "type_deadbeef",
             "--set-body", "public int Add(int a, int b) => a + b + 1;"]);
        Assert.Equal(Cs4AiResult.CodeStale, stale.ExitCode);

        var verify = await m.ExecuteAsync(["verify", sess]);
        Assert.Equal(Cs4AiResult.CodeOk, verify.ExitCode);

        var text = File.ReadAllText(Assert.Single(LogFiles(fx)));
        Assert.Contains($"exit code: {Cs4AiResult.CodeStale}", text);
        Assert.Contains("cs4ai update", text);
    }

    [Fact]
    public async Task Edit_InlineBody_NeverReadsStdin()
    {
        // The hang fix: stdin is opt-in. An inline --set-body edit (no --stdin) must never touch the
        // provided reader — passing one that throws on read proves it's left alone.
        using var fx = new FixtureSolution(Source);
        await using var m = M;

        var open = await m.ExecuteAsync(["session", fx.SlnxPath], new ThrowingReader());
        Assert.Equal(Cs4AiResult.CodeOk, open.ExitCode);
        var sess = SessionToken(open.Output!);

        var token = TypeTokenFromInspect(
            (await m.ExecuteAsync(["inspect", sess, "Acme.Calc"], new ThrowingReader())).Output!);
        var create = await m.ExecuteAsync(
            ["create", sess, "Acme.Calc.Sub(int,int)", "--token", token!,
             "--set-body", "public int Sub(int a, int b) => a - b;"],
            new ThrowingReader());

        Assert.Equal(Cs4AiResult.CodeOk, create.ExitCode); // never blocked on / read stdin
    }

    private sealed class ThrowingReader : TextReader
    {
        public override string ReadToEnd() => throw new InvalidOperationException("stdin must not be read");
        public override Task<string> ReadToEndAsync() =>
            throw new InvalidOperationException("stdin must not be read");
    }

    [Fact]
    public async Task Session_NoLogFlag_WritesNoFile()
    {
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        await m.ExecuteAsync(["inspect", sess, "Acme.Calc"]);
        var verify = await m.ExecuteAsync(["verify", sess]);
        Assert.Equal(Cs4AiResult.CodeOk, verify.ExitCode);

        Assert.Empty(LogFiles(fx)); // logging is off by default
    }

    [Fact]
    public async Task Create_LogFlag_ReturnsExit1()
    {
        using var fx = new FixtureSolution(Source);
        var (m, sess) = await OpenAsync(fx);
        await using var _ = m;

        var r = await m.ExecuteAsync(
            ["create", sess, "Acme.Thing", "--set-body", "public class Thing {}", "--log"]);
        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
    }
}
