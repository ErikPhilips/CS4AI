using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Phase 1.5 — a silent, compiler-guided post-op pass that removes the unambiguous detritus cs4ai's
/// own edit left behind, <b>before</b> the result is rendered, so the agent never sees noise cs4ai
/// created (and never has to ask for the cleanup).
/// <para>
/// The bar for a silent fix is high: it must be cs4ai-caused, unambiguous, and semantics-preserving.
/// The first (and currently only) fix clears it cleanly: a <c>using</c> directive the compiler flags
/// <b>CS0246</b> (namespace/type not found) imports something that doesn't exist, so it provides no
/// symbols — removing it cannot change resolution. This is the zero-false-positive subset; merely
/// <i>unnecessary</i> usings (CS8019, still valid) are deliberately left alone (the agent may want
/// them). Scoped to the documents this op changed — cs4ai tidies its own edits, not the whole repo.
/// </para>
/// </summary>
internal static class SelfHeal
{
    public static async Task<Solution> QuietBrokenUsingsAsync(
        Solution sol, Solution baseSol, IReadOnlySet<string>? roslynBaseline,
        Func<string?, string> relativize, CancellationToken ct)
    {
        if (ReferenceEquals(sol, baseSol)) return sol;

        var changedDocIds = sol.GetChanges(baseSol).GetProjectChanges()
            .SelectMany(pc => pc.GetChangedDocuments().Concat(pc.GetAddedDocuments()))
            .Distinct()
            .ToList();

        foreach (var docId in changedDocIds)
            sol = await QuietDocAsync(sol, docId, roslynBaseline, relativize, ct);
        return sol;
    }

    private static async Task<Solution> QuietDocAsync(
        Solution sol, DocumentId docId, IReadOnlySet<string>? roslynBaseline,
        Func<string?, string> relativize, CancellationToken ct)
    {
        var doc = sol.GetDocument(docId);
        if (doc is null) return sol;
        if (await doc.GetSyntaxRootAsync(ct) is not CompilationUnitSyntax root || root.Usings.Count == 0)
            return sol;

        var model = await doc.GetSemanticModelAsync(ct);
        if (model is null) return sol;

        // Spans the compiler reports as "namespace/type not found" — but ONLY the ones this session
        // introduced. A CS0246 already in the session baseline was not cs4ai-caused (the stated bar
        // for a silent fix): on a degraded workspace view every package using looks CS0246-broken,
        // and removing those is data loss, not cleanup (found live: a rename's write-through
        // deleted `using FluentAssertions;`/`using Xunit;` that the real build resolved fine).
        var brokenSpans = model.GetDiagnostics(cancellationToken: ct)
            .Where(d => d.Id == "CS0246")
            .Where(d => roslynBaseline is null || !roslynBaseline.Contains(BuildOutcomes.Key(d, relativize)))
            .Select(d => d.Location.SourceSpan)
            .ToList();
        if (brokenSpans.Count == 0) return sol;

        var dead = root.Usings
            .Where(u => brokenSpans.Any(s => u.Span.IntersectsWith(s)))
            .ToList();
        if (dead.Count == 0) return sol;

        // KeepEndOfLine, not KeepNoTrivia — the latter ate the newline between the last survivor
        // and the namespace declaration, gluing them onto one line.
        var newRoot = root.RemoveNodes(dead, SyntaxRemoveOptions.KeepEndOfLine)!;
        return doc.WithSyntaxRoot(newRoot).Project.Solution;
    }
}
