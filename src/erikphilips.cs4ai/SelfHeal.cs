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
        Solution sol, Solution baseSol, CancellationToken ct)
    {
        if (ReferenceEquals(sol, baseSol)) return sol;

        var changedDocIds = sol.GetChanges(baseSol).GetProjectChanges()
            .SelectMany(pc => pc.GetChangedDocuments().Concat(pc.GetAddedDocuments()))
            .Distinct()
            .ToList();

        foreach (var docId in changedDocIds)
            sol = await QuietDocAsync(sol, docId, ct);
        return sol;
    }

    private static async Task<Solution> QuietDocAsync(Solution sol, DocumentId docId, CancellationToken ct)
    {
        var doc = sol.GetDocument(docId);
        if (doc is null) return sol;
        if (await doc.GetSyntaxRootAsync(ct) is not CompilationUnitSyntax root || root.Usings.Count == 0)
            return sol;

        var model = await doc.GetSemanticModelAsync(ct);
        if (model is null) return sol;

        // Spans the compiler reports as "namespace/type not found".
        var brokenSpans = model.GetDiagnostics(cancellationToken: ct)
            .Where(d => d.Id == "CS0246")
            .Select(d => d.Location.SourceSpan)
            .ToList();
        if (brokenSpans.Count == 0) return sol;

        var dead = root.Usings
            .Where(u => brokenSpans.Any(s => u.Span.IntersectsWith(s)))
            .ToList();
        if (dead.Count == 0) return sol;

        var newRoot = root.RemoveNodes(dead, SyntaxRemoveOptions.KeepNoTrivia)!;
        return doc.WithSyntaxRoot(newRoot).Project.Solution;
    }
}
