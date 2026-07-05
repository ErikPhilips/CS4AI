using Microsoft.CodeAnalysis;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// One compile diagnostic on the staged view. <see cref="Status"/> reconciles the absolute list
/// against the delta rollup: <c>new</c> means this session introduced it, <c>preexisting</c> means
/// it was already there at session open. The agent reads the tag to recover correctly — fix what it
/// broke, ignore the floor.
/// </summary>
internal sealed record BuildDiagnostic(
    string Severity,   // "error" | "warning"
    string Code,       // e.g. "CS0246"
    string File,       // repo-relative
    int Line,          // 1-based
    string Message,
    string Status);    // "new" | "preexisting"

/// <summary>
/// The second result axis (the exit code is the first): "is the staged code buildable, and did
/// <i>this session</i> change that?" The <see cref="Rollup"/> is a delta over errors only; the
/// <see cref="Diagnostics"/> list is absolute (every error + warning, IDE-style), each tagged. The
/// count fields are computed once here so both renderers (framed block + JSON) read them off the
/// record rather than recounting — they can't drift.
/// </summary>
internal sealed record BuildOutcome(
    string Rollup,     // "passed" | "passed_with_warnings" | "failed"
    IReadOnlyList<BuildDiagnostic> Diagnostics,
    int NewErrors,
    int NewWarnings,
    int Preexisting,
    int Resolved);     // baseline keys no longer present — pre-existing problems this session cleared

/// <summary>
/// Compiles the staged solution and turns Roslyn diagnostics into a <see cref="BuildOutcome"/>.
/// Severity comes free from Roslyn (the project's <c>.editorconfig</c> / analyzer config /
/// <c>TreatWarningsAsErrors</c> already resolve a warning to <see cref="DiagnosticSeverity.Error"/>
/// before we see it). The <b>baseline keyset is frozen at session open</b>; every later compute
/// tags against it, so a diagnostic the session introduced is <c>new</c> even after a structure-op
/// reload moves the <see cref="Solution"/> object.
/// </summary>
internal static class BuildOutcomes
{
    /// <summary>A clean, empty outcome (no session / nothing to report).</summary>
    public static readonly BuildOutcome Empty = new("passed", [], 0, 0, 0, 0);

    /// <summary>Line-free identity key so a diagnostic that merely shifted lines isn't counted as
    /// "new". Same scheme the old errors-only delta used, now spanning warnings too.</summary>
    private static string Key(Diagnostic d, Func<string?, string> relativize) =>
        $"{d.Id}|{relativize(d.Location.GetLineSpan().Path)}|{d.GetMessage()}";

    /// <summary>The frozen baseline: every error+warning key present at session open (and after a
    /// structure-op rebase, the caller keeps the original — it is NOT re-seeded). Also returns the
    /// Roslyn ERROR count so the caller can compare the workspace view against the real build's
    /// (a large excess means the load resolved references against a broken/unrestored tree).</summary>
    public static async Task<(HashSet<string> keys, int errorCount)> BaselineKeysAsync(
        Solution sol, Func<string?, string> relativize, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var errors = 0;
        await foreach (var d in DiagnosticsAsync(sol, ct))
        {
            keys.Add(Key(d, relativize));
            if (d.Severity == DiagnosticSeverity.Error) errors++;
        }
        return (keys, errors);
    }

    public static async Task<BuildOutcome> ComputeAsync(
        Solution staged, IReadOnlySet<string> baseline, Func<string?, string> relativize, CancellationToken ct)
    {
        var diagnostics = new List<BuildDiagnostic>();
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int newErrors = 0, newWarnings = 0, preexisting = 0;

        await foreach (var d in DiagnosticsAsync(staged, ct))
        {
            var key = Key(d, relativize);
            currentKeys.Add(key);
            if (!seen.Add(key)) continue; // dedup by signature (a duplicate isn't a new problem)

            var isNew = !baseline.Contains(key);
            var span = d.Location.GetLineSpan();
            var severity = d.Severity == DiagnosticSeverity.Error ? "error" : "warning";
            diagnostics.Add(new BuildDiagnostic(
                severity, d.Id, relativize(span.Path), span.StartLinePosition.Line + 1,
                d.GetMessage(), isNew ? "new" : "preexisting"));

            if (!isNew) preexisting++;
            else if (severity == "error") newErrors++;
            else newWarnings++;
        }

        var resolved = baseline.Count(k => !currentKeys.Contains(k));
        var rollup = newErrors > 0 ? "failed"
                   : newWarnings > 0 ? "passed_with_warnings"
                   : "passed";

        SortDiagnostics(diagnostics); // stable, scannable: errors before warnings, then file/line
        return new BuildOutcome(rollup, diagnostics, newErrors, newWarnings, preexisting, resolved);
    }

    // ── full-build vocabulary (CS*+NU*+MSB*) — graph edits + verify ──────────────────────────────

    private static string BuildKey(BuildAndTest.Diag d, Func<string?, string> relativize) =>
        $"{d.Code}|{relativize(d.Path)}|{d.Message}";

    /// <summary>The full keyset from a real <c>dotnet build</c> — the <c>BuildBaseline</c> for graph
    /// edits / verify (a different vocabulary from Roslyn's, hence its own baseline).</summary>
    public static HashSet<string> BuildBaselineKeys(
        IReadOnlyList<BuildAndTest.Diag> diags, Func<string?, string> relativize)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in diags) keys.Add(BuildKey(d, relativize));
        return keys;
    }

    /// <summary>Map a real-build diagnostic set into a <see cref="BuildOutcome"/>, tagged new/
    /// preexisting against the full <paramref name="baseline"/>. Same rollup/sort rules as the Roslyn
    /// path; this one can carry NU*/MSBuild diagnostics.</summary>
    public static BuildOutcome FromBuild(
        IReadOnlyList<BuildAndTest.Diag> diags, IReadOnlySet<string> baseline, Func<string?, string> relativize)
    {
        var list = new List<BuildDiagnostic>();
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int newErrors = 0, newWarnings = 0, preexisting = 0;

        foreach (var d in diags)
        {
            var key = BuildKey(d, relativize);
            currentKeys.Add(key);
            if (!seen.Add(key)) continue;

            var isNew = !baseline.Contains(key);
            list.Add(new BuildDiagnostic(d.Severity, d.Code, relativize(d.Path), d.Line, d.Message,
                isNew ? "new" : "preexisting"));
            if (!isNew) preexisting++;
            else if (d.Severity == "error") newErrors++;
            else newWarnings++;
        }

        var resolved = baseline.Count(k => !currentKeys.Contains(k));
        var rollup = newErrors > 0 ? "failed" : newWarnings > 0 ? "passed_with_warnings" : "passed";
        SortDiagnostics(list);
        return new BuildOutcome(rollup, list, newErrors, newWarnings, preexisting, resolved);
    }

    private static void SortDiagnostics(List<BuildDiagnostic> diagnostics) => diagnostics.Sort((a, b) =>
    {
        int s = string.CompareOrdinal(a.Severity, b.Severity); // "error" < "warning"
        if (s != 0) return s;
        int f = string.CompareOrdinal(a.File, b.File);
        return f != 0 ? f : a.Line.CompareTo(b.Line);
    });

    /// <summary>Every error/warning across the solution's projects (Hidden/Info dropped — IDE shows
    /// errors + warnings).</summary>
    private static async IAsyncEnumerable<Diagnostic> DiagnosticsAsync(
        Solution sol, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var project in sol.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            foreach (var d in compilation.GetDiagnostics(ct))
                if (d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
                    yield return d;
        }
    }
}
