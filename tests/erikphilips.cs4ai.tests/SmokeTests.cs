using ErikPhilips.Cs4Ai;
using Xunit;

namespace ErikPhilips.Cs4Ai.Tests;

/// <summary>
/// Smoke tests covering the meta paths, exit-code grammar, routing-level argument validation,
/// and skill emission. Heavier verb + session-protocol tests live in <see cref="VerbTests"/>.
/// </summary>
public class SmokeTests
{
    private static Cs4AiEngine M => new(inProcess: true);

    [Fact]
    public async Task NoArgs_ReturnsUsage_WithCs4AiInOutput()
    {
        await using var m = M;
        var r = await m.ExecuteAsync([]);
        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
        Assert.NotNull(r.Output);
        Assert.Contains("cs4ai", r.Output!);
    }

    [Fact]
    public async Task HelpFlag_ReturnsUsage()
    {
        await using var m = M;
        var r = await m.ExecuteAsync(["--help"]);
        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
        Assert.Contains("Usage:", r.Output);
    }

    [Fact]
    public async Task Version_ReturnsOk_WithVersionString()
    {
        await using var m = M;
        var r = await m.ExecuteAsync(["--version"]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.StartsWith("cs4ai ", r.Output);
    }

    [Fact]
    public async Task ShowReadme_ReturnsOk_WithEmbeddedContent()
    {
        await using var m = M;
        var r = await m.ExecuteAsync(["--show-readme"]);
        Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
        Assert.NotNull(r.Output);
        Assert.Contains("cs4ai", r.Output!);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsUsage()
    {
        await using var m = M;
        var r = await m.ExecuteAsync(["frobnicate", "x.slnx"]);
        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
        Assert.Contains("unknown command", r.Error);
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("discover")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("rename")]
    [InlineData("move")]
    [InlineData("delete")]
    [InlineData("build")]
    [InlineData("verify")]
    public async Task SessionRoutedVerbWithoutToken_ReturnsUsageError(string verb)
    {
        // Every session-routed verb leads with a sess_ token; omitting it is an exit-1 usage error
        // that names the session token (not a silent route).
        await using var m = M;
        var r = await m.ExecuteAsync([verb]);
        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
        Assert.NotNull(r.Error);
        Assert.Contains("sess_", r.Error!);
    }

    [Theory]
    [InlineData("inspect")]
    [InlineData("discover")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("rename")]
    [InlineData("move")]
    [InlineData("delete")]
    [InlineData("session")]
    [InlineData("build")]
    [InlineData("run-test")]
    [InlineData("verify")]
    [InlineData("init")]
    [InlineData("stop-daemon")]
    [InlineData("reload")]
    public async Task VerbHelpFlag_ReturnsUsage_WithoutNeedingASolution(string verb)
    {
        await using var m = M;
        var r = await m.ExecuteAsync([verb, "--help"]);
        Assert.Equal(Cs4AiResult.CodeUsage, r.ExitCode);
        Assert.NotNull(r.Output);
        Assert.Contains("cs4ai", r.Output!);
    }

    [Fact]
    public async Task CreateSkill_NoArg_WritesToDefaultPath()
    {
        var tmpRoot = Path.Combine(Path.GetTempPath(), "cs4ai-test-" + Guid.NewGuid().ToString("N"));
        var prev = Directory.GetCurrentDirectory();
        try
        {
            Directory.CreateDirectory(tmpRoot);
            Directory.SetCurrentDirectory(tmpRoot);

            await using var m = M;
            var r = await m.ExecuteAsync(["--create-skill"]);
            Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);

            var expected = Path.Combine(tmpRoot, ".claude", "skills", "cs4ai", "SKILL.md");
            Assert.True(File.Exists(expected), $"expected skill at {expected}");
            var content = File.ReadAllText(expected);
            Assert.Contains("name: cs4ai", content);
            Assert.Contains("# cs4ai (CLI)", content);
            // The skill must carry the v2 session protocol as reference lines.
            Assert.Contains("session", content);
            Assert.Contains("sess_", content);
            Assert.Contains("--token", content);
            Assert.Contains("7 no session", content);
            Assert.Contains("Use INSTEAD OF Grep/Read/Edit", content);
        }
        finally
        {
            Directory.SetCurrentDirectory(prev);
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CreateSkill_TargetDir_WritesToDirSlashCs4aiSlashSkillMd()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "cs4ai-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var m = M;
            var r = await m.ExecuteAsync(["--create-skill", tmp]);
            Assert.Equal(Cs4AiResult.CodeOk, r.ExitCode);
            Assert.True(File.Exists(Path.Combine(tmp, "cs4ai", "SKILL.md")));
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void Cs4AiResult_ExitCodes_MatchSpec()
    {
        Assert.Equal(0, Cs4AiResult.CodeOk);
        Assert.Equal(1, Cs4AiResult.CodeUsage);
        Assert.Equal(2, Cs4AiResult.CodeFileOrParse);
        Assert.Equal(3, Cs4AiResult.CodeAmbiguous);
        Assert.Equal(4, Cs4AiResult.CodeNotFound);
        Assert.Equal(5, Cs4AiResult.CodeStale);
        Assert.Equal(6, Cs4AiResult.CodeNeedsConfig);
        Assert.Equal(7, Cs4AiResult.CodeNoSession);
    }

    [Fact]
    public void SkillFile_CommandTemplates_AllPrefixed()
    {
        // A field agent copied a bare `session "Foo.slnx"` from the skill straight into bash →
        // exit 127 (issue #12). Every copy-able command template must lead with `cs4ai `.
        string[] verbs = ["session", "inspect", "discover", "create", "update", "rename", "move",
                          "delete", "create-project", "add-reference", "build", "run-test", "verify",
                          "init", "reload", "stop-daemon"];
        foreach (var v in verbs)
        {
            Assert.DoesNotContain($"- `{v} ", Help.SkillFile);        // bullet template, bare
            Assert.DoesNotContain($"`{v} <sess", Help.SkillFile);     // inline template, bare
        }
        Assert.Contains("- `cs4ai session ", Help.SkillFile);         // the taught entry point
    }

    [Fact]
    public void PipeKey_IsStable_AndPrefixesSessionTokens()
    {
        var key1 = DaemonProtocol.PipeKeyFor(@"C:\repos\Foo\Foo.slnx");
        var key2 = DaemonProtocol.PipeKeyFor(@"C:\repos\Foo\Foo.slnx");
        Assert.Equal(key1, key2);
        Assert.Equal(12, key1.Length);

        Assert.Equal(key1, DaemonProtocol.PipeKeyFromSessionToken($"sess_{key1}.deadbeef01234567"));
        Assert.Null(DaemonProtocol.PipeKeyFromSessionToken("not-a-token"));
        Assert.Null(DaemonProtocol.PipeKeyFromSessionToken($"{key1}.deadbeef")); // missing sess_ prefix
        Assert.Null(DaemonProtocol.PipeKeyFromSessionToken("sess_short.abc"));
    }
}
