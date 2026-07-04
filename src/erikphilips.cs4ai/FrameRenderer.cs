using Microsoft.CodeAnalysis;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The one renderer for the <b>TypeFrame</b> result family (version2.md, <i>Output contract</i>):
/// read (<c>inspect</c>), write-restate (create/update/rename/delete/move), and stale (exit 5) all
/// emit the same frame, so the agent learns one shape and a plugin macro composes by <b>concatenating
/// line-streams</b> — never parsing. A frame is header-identical-to-footer, and the header leads with
/// the <b>op that produced it</b> so a concatenated result is readable block-by-block:
/// <code>
/// [&lt;op&gt; &lt;address&gt; · &lt;path&gt; · N lines · type_&lt;token&gt;]
/// &lt;body&gt;
/// [&lt;op&gt; &lt;address&gt; · &lt;path&gt; · N lines · type_&lt;token&gt;]
/// </code>
/// The body is the full source for create/update/inspect, or a one-line delta (<c>A -&gt; B</c>,
/// <c>deleted: X</c>) for rename/delete/move — but the frame (and thus the fresh token, in its usual
/// place) is always present on success. The token lives in the frame metadata, never the body.
/// </summary>
internal static class FrameRenderer
{
    /// <summary>
    /// A <b>group frame</b>: a bare <c>[label]</c> header==footer wrapping a sub-stream. A phase-2
    /// plugin macro emits its op name as a group around the concatenation of the native
    /// (<see cref="Frame"/>) blocks it produced — e.g.
    /// <code>
    /// [create-queryrequest]
    /// [create Acme.FooRequest · … · type_…]
    /// …
    /// [create Acme.FooHandler · … · type_…]
    /// [create-queryrequest]
    /// </code>
    /// so a nested result stays readable and parse-free (the agent reads it as one stream; the host
    /// composes by concatenation). Not used by native phase-1 ops; it's the plugin-composition seam.
    /// </summary>
    public static IEnumerable<string> Group(string label, IEnumerable<string> inner)
    {
        var bar = $"[{label}]";
        yield return bar;
        foreach (var line in inner) yield return line;
        yield return bar;
    }

    /// <summary>A single framed block. <paramref name="op"/> leads the header (the verb that produced
    /// it: create/update/rename/delete/move/inspect/stale); <paramref name="path"/> is repo-relative;
    /// the line count describes the body between the frames.</summary>
    public static IEnumerable<string> Frame(string op, string address, string path, string body, string token)
    {
        body = body.Replace("\r\n", "\n").TrimEnd('\n');
        var n = body.Length == 0 ? 0 : body.Count(c => c == '\n') + 1;
        var bar = $"[{op} {address} · {path} · {n} lines · {token}]";
        yield return bar;
        if (body.Length > 0) yield return body;
        yield return bar;
    }

    /// <summary>A <b>container frame</b> for an echoed project/solution file (slnx/csproj). Same
    /// op-led, header==footer shape as <see cref="Frame"/> but <b>without</b> the staleness
    /// <c>token</c> — containers have no type token. So a structure op reads exactly like a semantic
    /// edit: a status line, these frames, then the <c>[build …]</c> block.</summary>
    public static IEnumerable<string> ContainerFrame(string op, string name, string path, string body)
    {
        body = body.Replace("\r\n", "\n").TrimEnd('\n');
        var n = body.Length == 0 ? 0 : body.Count(c => c == '\n') + 1;
        var bar = $"[{op} {name} · {path} · {n} lines]";
        yield return bar;
        if (body.Length > 0) yield return body;
        yield return bar;
    }

    /// <summary>Full-source frames for a type — one per declaration file (partials). Used by
    /// <c>inspect</c> and create/update write-restate. XML doc comments ride along (they're leading
    /// trivia of the declaration).</summary>
    public static IEnumerable<string> FullType(
        INamedTypeSymbol type, Func<string?, string> relativize, string op)
    {
        var address = AddressResolver.Render(type);
        var token = CSEngine.TypeTokenPrefix + TokenBuilder.TokenString(type);
        foreach (var declRef in type.DeclaringSyntaxReferences
                                    .OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal))
        {
            var source = declRef.GetSyntax().ToFullString();
            var path = relativize(declRef.SyntaxTree.FilePath);
            foreach (var line in Frame(op, address, path, source, token)) yield return line;
        }
    }

    /// <summary>The <b>build axis</b> block (the second result axis, distinct from the exit code):
    /// a header carrying the rollup + delta counts, then one line per diagnostic (<c>+</c> = new this
    /// session, blank = pre-existing), then a short footer. Always emits the header so an edit's build
    /// state is one glance away; lists diagnostics only when present. Counts are read straight off the
    /// record — never recounted from <see cref="BuildOutcome.Diagnostics"/> — so this block and the
    /// JSON <c>buildOutcome</c> field cannot drift.</summary>
    public static IEnumerable<string> BuildBlock(BuildOutcome o)
    {
        var parts = new List<string>
        {
            Plural(o.NewErrors, "new error"),
            Plural(o.NewWarnings, "new warning"),
            $"{o.Preexisting} preexisting", // count-word, no plural 's'
        };
        if (o.Resolved > 0) parts.Add($"{o.Resolved} resolved");

        yield return $"[build {o.Rollup} · {string.Join(" · ", parts)}]";
        foreach (var d in o.Diagnostics)
        {
            var marker = d.Status == "new" ? "+ " : "  ";
            yield return $"{marker}{d.Severity} {d.Code} · {d.File}:{d.Line} · {d.Message}";
        }
        yield return $"[build {o.Rollup}]";
    }

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>A delta frame: the (post-edit) type's frame carrying its fresh token, wrapping a
    /// one-line delta as the body — for rename/delete-member/move, where re-dumping the body would
    /// be redundant.</summary>
    public static IEnumerable<string> DeltaType(
        INamedTypeSymbol type, string delta, Func<string?, string> relativize, string op)
    {
        var address = AddressResolver.Render(type);
        var token = CSEngine.TypeTokenPrefix + TokenBuilder.TokenString(type);
        var path = relativize(type.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath);
        return Frame(op, address, path, delta, token);
    }
}
