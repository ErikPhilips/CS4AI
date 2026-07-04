using ErikPhilips.Cs4Ai;
using Xunit;

namespace ErikPhilips.Cs4Ai.Tests;

/// <summary>
/// The operation grammar (<see cref="OperationGrammar"/>) is the spec the flat <see cref="Operation"/>
/// bag is judged against. These tests hammer it hardest (version2.md, <i>Verification</i>): the
/// exhaustiveness meta-test, required-present / optional-accepted / forbidden-rejected, and the
/// flagship <c>delete + Body → exit 1</c> case.
/// </summary>
public class OperationGrammarTests
{
    // ── Meta-test: every (op, field) pair is classified ──────────────────────────────────────────

    [Fact]
    public void EveryOpFieldPair_IsClassified()
    {
        var unclassified = new List<string>();
        foreach (var op in Ops.All)
            foreach (OperationField field in Enum.GetValues<OperationField>())
                if (OperationGrammar.ClassOf(op, field) is null)
                    unclassified.Add($"{op}.{field}");

        Assert.True(unclassified.Count == 0,
            "unclassified (op, field) pairs — a spec hole: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void EveryRegisteredOp_IsClassified()
    {
        var classified = OperationGrammar.ClassifiedOps.ToHashSet();
        foreach (var op in Ops.All)
            Assert.Contains(op, classified);
        Assert.Equal(Ops.All.Count, classified.Count);
    }

    // ── Required present / missing ───────────────────────────────────────────────────────────────

    [Fact]
    public void Create_WithDestinationAndBody_IsStructurallyValid()
    {
        var op = new Operation { Op = Ops.Create, Destination = "Ns.Foo", Body = "class Foo {}" };
        Assert.Null(OperationGrammar.ValidateStructural(op));
    }

    [Fact]
    public void Create_MissingBody_IsExit1()
    {
        var op = new Operation { Op = Ops.Create, Destination = "Ns.Foo" };
        var r = OperationGrammar.ValidateStructural(op);
        Assert.NotNull(r);
        Assert.Equal(Cs4AiResult.CodeUsage, r!.Value.ExitCode);
    }

    [Fact]
    public void Delete_WithSource_IsStructurallyValid()
    {
        var op = new Operation { Op = Ops.Delete, Source = "Ns.Foo.Bar()" };
        Assert.Null(OperationGrammar.ValidateStructural(op));
    }

    // ── Forbidden present → exit 1 (rejects, never silently ignores) ─────────────────────────────

    [Fact]
    public void Delete_WithBody_IsExit1()   // the flagship forbidden-field case
    {
        var op = new Operation { Op = Ops.Delete, Source = "Ns.Foo.Bar()", Body = "void Bar() {}" };
        var r = OperationGrammar.ValidateStructural(op);
        Assert.NotNull(r);
        Assert.Equal(Cs4AiResult.CodeUsage, r!.Value.ExitCode);
    }

    [Fact]
    public void Create_WithNamespace_IsExit1()   // namespace comes from the FQN, not a flag
    {
        var op = new Operation { Op = Ops.Create, Destination = "Ns.Foo", Body = "class Foo {}", Namespace = "Ns" };
        var r = OperationGrammar.ValidateStructural(op);
        Assert.NotNull(r);
        Assert.Equal(Cs4AiResult.CodeUsage, r!.Value.ExitCode);
    }

    // ── Optional accepted; update's at-least-one rule ───────────────────────────────────────────

    [Fact]
    public void Update_CommentOnly_IsStructurallyValid()
    {
        var op = new Operation { Op = Ops.Update, Source = "Ns.Foo.Bar()", XmlComment = "/// <summary>x</summary>" };
        Assert.Null(OperationGrammar.ValidateStructural(op));
    }

    [Fact]
    public void Update_NoFacet_IsExit1()   // update must do *something*
    {
        var op = new Operation { Op = Ops.Update, Source = "Ns.Foo.Bar()" };
        var r = OperationGrammar.ValidateStructural(op);
        Assert.NotNull(r);
        Assert.Equal(Cs4AiResult.CodeUsage, r!.Value.ExitCode);
    }

    [Fact]
    public void Update_AttributesOnly_IsStructurallyValid()   // --set-attributes counts as a facet
    {
        var op = new Operation { Op = Ops.Update, Source = "Ns.Foo.Bar()", Attributes = "[Obsolete]" };
        Assert.Null(OperationGrammar.ValidateStructural(op));
    }

    [Fact]
    public void Delete_WithAttributes_IsExit1()   // delete = remove the symbol; attributes forbidden
    {
        var op = new Operation { Op = Ops.Delete, Source = "Ns.Foo.Bar()", Attributes = "[Obsolete]" };
        var r = OperationGrammar.ValidateStructural(op);
        Assert.NotNull(r);
        Assert.Equal(Cs4AiResult.CodeUsage, r!.Value.ExitCode);
    }

    // ── --set-file: optional on update/rename, forbidden elsewhere ──────────────────────────────

    [Fact]
    public void Update_SetFileOnly_IsStructurallyValid()   // --set-file counts as a facet
    {
        var op = new Operation { Op = Ops.Update, Source = "Ns.Foo", File = "Foo.cs" };
        Assert.Null(OperationGrammar.ValidateStructural(op));
    }

    [Fact]
    public void Rename_WithSetFile_IsStructurallyValid()
    {
        var op = new Operation { Op = Ops.Rename, Source = "Ns.Foo", Destination = "Bar", File = "Bar.cs" };
        Assert.Null(OperationGrammar.ValidateStructural(op));
    }

    [Theory]
    [InlineData(Ops.Delete)]
    [InlineData(Ops.Move)]
    [InlineData(Ops.Create)]
    public void SetFile_OnUnsupportedOp_IsExit1(string op)
    {
        var bag = op switch
        {
            Ops.Create => new Operation { Op = op, Destination = "Ns.Foo", Body = "class Foo {}", File = "Foo.cs" },
            Ops.Move   => new Operation { Op = op, Source = "Ns.Foo.Bar()", Destination = "Ns.Baz", File = "x.cs" },
            _          => new Operation { Op = op, Source = "Ns.Foo", File = "x.cs" },
        };
        var r = OperationGrammar.ValidateStructural(bag);
        Assert.NotNull(r);
        Assert.Equal(Cs4AiResult.CodeUsage, r!.Value.ExitCode);
    }

    // ── Unknown op ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnknownOp_IsExit1()
    {
        var op = new Operation { Op = "frobnicate", Source = "Ns.Foo" };
        var r = OperationGrammar.ValidateStructural(op);
        Assert.NotNull(r);
        Assert.Equal(Cs4AiResult.CodeUsage, r!.Value.ExitCode);
    }

    // ── Help is drawn from the same table (no drift) ────────────────────────────────────────────

    [Fact]
    public void HelpFor_Create_ListsDestinationAndBodyRequired_PathOptional()
    {
        var (required, optional) = OperationGrammar.HelpFor(Ops.Create);
        Assert.Contains("<destination>", required);
        Assert.Contains("--set-body", required);
        Assert.Contains("--path", optional);
        Assert.DoesNotContain("--set-namespace", required.Concat(optional)); // forbidden → unlisted
    }
}
