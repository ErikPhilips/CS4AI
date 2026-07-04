using ErikPhilips.Cs4Ai;
using Xunit;

namespace ErikPhilips.Cs4Ai.Tests;

/// <summary>
/// The frame grammar: a type frame (header == footer, op-led, with metadata) and a group frame
/// (bare label wrapping a sub-stream) that a plugin macro uses to compose native frames by
/// concatenation — parse-free.
/// </summary>
public class FrameRendererTests
{
    [Fact]
    public void Frame_HeaderEqualsFooter_LeadsWithOp()
    {
        var lines = FrameRenderer.Frame("create", "Acme.Foo", "src/Foo.cs", "class Foo {}", "type_abc").ToList();
        Assert.Equal("[create Acme.Foo · src/Foo.cs · 1 lines · type_abc]", lines[0]);
        Assert.Equal(lines[0], lines[^1]); // footer identical to header
        Assert.Contains("class Foo {}", lines);
    }

    [Fact]
    public void BuildBlock_HeaderCounts_ComeStraightOffTheRecord_NotRecounted()
    {
        // Render parity: the framed header counts must equal the record's count fields, never a
        // recount of Diagnostics — so the framed block and the JSON buildOutcome can't drift.
        var outcome = new BuildOutcome(
            "passed_with_warnings",
            [
                new BuildDiagnostic("warning", "CS0168", "src/Foo.cs", 12, "unused 'x'", "new"),
                new BuildDiagnostic("warning", "CS0219", "src/Bar.cs", 9, "assigned 'y'", "preexisting"),
            ],
            NewErrors: 0, NewWarnings: 1, Preexisting: 1, Resolved: 2);

        var lines = FrameRenderer.BuildBlock(outcome).ToList();

        Assert.Equal("[build passed_with_warnings · 0 new errors · 1 new warning · 1 preexisting · 2 resolved]", lines[0]);
        Assert.Equal("[build passed_with_warnings]", lines[^1]);
        Assert.Contains(lines, l => l.StartsWith("+ warning CS0168 · src/Foo.cs:12 · ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("  warning CS0219 · src/Bar.cs:9 · ", StringComparison.Ordinal));
    }

    [Fact]
    public void Group_WrapsInnerFrames_AsPluginMacroComposition()
    {
        var request  = FrameRenderer.Frame("create", "Acme.FooRequest",  "src/Foo.cs", "x", "type_1");
        var handler  = FrameRenderer.Frame("create", "Acme.FooHandler",  "src/Foo.cs", "y", "type_2");
        var inner = request.Concat(handler);

        var lines = FrameRenderer.Group("create-queryrequest", inner).ToList();

        Assert.Equal("[create-queryrequest]", lines[0]);
        Assert.Equal("[create-queryrequest]", lines[^1]);
        Assert.Contains(lines, l => l.StartsWith("[create Acme.FooRequest ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("[create Acme.FooHandler ", StringComparison.Ordinal));
    }
}
