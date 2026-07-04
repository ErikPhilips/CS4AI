using ErikPhilips.Cs4Ai;
using Xunit;

namespace ErikPhilips.Cs4Ai.Tests;

/// <summary>
/// Front-half tests for the edit interpreters: argv → <see cref="SemanticOperation"/>. They cover
/// the mapping, session-token-first extraction with per-slot prefix validation, and grammar-driven
/// unknown-flag rejection. Engine execution of the produced ops is exercised separately (Step 4).
/// </summary>
public class InterpreterTests
{
    private static async Task<(SemanticOperation? op, Cs4AiResult? err)> Parse(string verb, params string[] args)
    {
        var interp = EditInterpreters.For(verb);
        Assert.NotNull(interp);
        return await interp!.ParseAsync(args, stdin: null, ct: default);
    }

    // ── create ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_MapsFqnPathAndBody()
    {
        var (op, err) = await Parse("create",
            "sess_abc.123", "Acme.Hello.Person", "--path", "app", "--set-body", "class Person {}");
        Assert.Null(err);
        Assert.NotNull(op);
        Assert.Equal("sess_abc.123", op!.SessionToken);
        Assert.Equal(Ops.Create, op.Op.Op);
        Assert.Equal("Acme.Hello.Person", op.Op.Destination);
        Assert.Equal("app", op.Op.Path);
        Assert.Equal("class Person {}", op.Op.Body);
        Assert.Null(op.Op.Source); // create never sets Source
    }

    [Fact]
    public async Task Create_MissingBody_IsExit1()
    {
        var (op, err) = await Parse("create", "sess_abc.123", "Acme.Hello.Person");
        Assert.Null(op);
        Assert.NotNull(err);
        Assert.Equal(Cs4AiResult.CodeUsage, err!.Value.ExitCode);
    }

    // ── update ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_MapsSourceAndFacets()
    {
        var (op, err) = await Parse("update",
            "sess_abc.123", "Ns.Foo.Bar()", "--token", "type_deadbeef", "--set-comment", "/// x");
        Assert.Null(err);
        Assert.NotNull(op);
        Assert.Equal("type_deadbeef", op!.TypeToken);
        Assert.Equal("Ns.Foo.Bar()", op.Op.Source);
        Assert.Equal("/// x", op.Op.XmlComment);
        Assert.Null(op.Op.Body);
    }

    // ── rename / move (second positional) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_MapsSourceAndNewName()
    {
        var (op, err) = await Parse("rename", "sess_abc.123", "Ns.Foo.Bar", "Baz", "--token", "type_dead");
        Assert.Null(err);
        Assert.Equal("Ns.Foo.Bar", op!.Op.Source);
        Assert.Equal("Baz", op.Op.Destination); // new name rides Destination
    }

    [Fact]
    public async Task Update_MapsSetFile()
    {
        var (op, err) = await Parse("update",
            "sess_abc.123", "Ns.Foo", "--token", "type_dead", "--set-file", "Sub/Foo.cs", "--in-file", "Old.cs");
        Assert.Null(err);
        Assert.Equal("Sub/Foo.cs", op!.Op.File);
        Assert.Equal("Old.cs", op.Op.InFile);
    }

    [Fact]
    public async Task Rename_MapsSetFile()
    {
        var (op, err) = await Parse("rename",
            "sess_abc.123", "Ns.Foo", "Bar", "--token", "type_dead", "--set-file", "Bar.cs");
        Assert.Null(err);
        Assert.Equal("Bar", op!.Op.Destination);
        Assert.Equal("Bar.cs", op.Op.File);
    }

    [Fact]
    public async Task Move_MapsSourceAndTarget()
    {
        var (op, err) = await Parse("move", "sess_abc.123", "Ns.Foo.Bar()", "Ns.Baz", "--token", "type_dead");
        Assert.Null(err);
        Assert.Equal("Ns.Foo.Bar()", op!.Op.Source);
        Assert.Equal("Ns.Baz", op.Op.Destination);
    }

    // ── unknown-flag rejection (the ArgParse silent-swallow fix) ───────────────────────────────────

    [Fact]
    public async Task Create_UnknownFlag_IsExit1()
    {
        var (op, err) = await Parse("create",
            "sess_abc.123", "Ns.Foo", "--set-body", "class Foo {}", "--namespace", "Ns");
        Assert.Null(op);
        Assert.NotNull(err);
        Assert.Equal(Cs4AiResult.CodeUsage, err!.Value.ExitCode);
        Assert.Contains("--namespace", err.Value.Error ?? err.Value.Output ?? "");
    }

    [Fact]
    public async Task Delete_BodyFlag_IsRejectedByInterpreter()   // --set-body forbidden for delete
    {
        var (op, err) = await Parse("delete",
            "sess_abc.123", "Ns.Foo.Bar()", "--token", "type_dead", "--set-body", "void Bar(){}");
        Assert.Null(op);
        Assert.NotNull(err);
        Assert.Equal(Cs4AiResult.CodeUsage, err!.Value.ExitCode);
    }

    // ── per-slot token-prefix validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SessionSlot_WithTypeToken_IsExit1()
    {
        var (op, err) = await Parse("delete", "type_abc", "Ns.Foo");
        Assert.Null(op);
        Assert.NotNull(err);
        Assert.Equal(Cs4AiResult.CodeUsage, err!.Value.ExitCode);
    }

    [Fact]
    public async Task TokenSlot_WithSessionToken_IsExit1()
    {
        var (op, err) = await Parse("delete", "sess_abc.123", "Ns.Foo", "--token", "sess_oops");
        Assert.Null(op);
        Assert.NotNull(err);
        Assert.Equal(Cs4AiResult.CodeUsage, err!.Value.ExitCode);
    }

    [Fact]
    public async Task MissingSessionToken_IsExit1()
    {
        var (op, err) = await Parse("delete");
        Assert.Null(op);
        Assert.NotNull(err);
        Assert.Equal(Cs4AiResult.CodeUsage, err!.Value.ExitCode);
    }
}
