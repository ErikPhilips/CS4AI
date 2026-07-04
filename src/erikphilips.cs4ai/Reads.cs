using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The two read verbs (version2.md, <i>Reads</i>): <c>discover</c> (breadth — census + directional
/// references; v1's <c>usages</c> folds in here) and <c>inspect</c> (one type, whole, XML comments
/// included, framed; v1's <c>trace</c> folds in via <see cref="StackFrameParser"/>). Both are
/// session-gated (lead with the <c>sess_</c> token) and see the <b>staged fork</b>, so an agent sees
/// its own uncommitted edits.
/// </summary>
internal static class Reads
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  inspect — one type, whole, framed. Eats FQN / file:line / raw stack frame.
    // ─────────────────────────────────────────────────────────────────────────────

    public static async Task<Cs4AiResult> RunInspect(SolutionHost host, string[] rest, CancellationToken ct)
    {
        var p = ArgParse.Parse(rest);
        if (p.Help) return Cs4AiResult.Usage(Help.Inspect);

        var sessToken = p.Positionals.Count > 0 ? p.Positionals[0] : null;
        var (view, viewErr) = await host.ResolveViewAsync(sessToken, ct);
        if (viewErr is { } ve) return ve;

        // Trace mode: --from a stack-trace file → resolve every non-framework frame to its type.
        if (p.From is not null)
            return await InspectTraceAsync(host, view!, p.From, ct);

        if (p.Positionals.Count < 2)
            return Cs4AiResult.UsageError(
                "inspect: usage: cs4ai inspect <sess-token> <address> (FQN, file:line, or a raw stack frame)");

        var address = StackFrameParser.CleanAddress(p.Positionals[1]);
        var (matches, addrErr) = await AddressResolver.ResolveManyAsync(view!, address, ct);
        if (addrErr is { } ae) return ae;
        if (matches.Count == 0)
            return Cs4AiResult.NotFound($"address not found: '{p.Positionals[1]}'");

        // Roll every hit up to its declaring type (inspect's unit is the type), de-duped by DocId.
        var types = new List<INamedTypeSymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in matches)
        {
            var t = m as INamedTypeSymbol ?? m.ContainingType;
            if (t is null) continue;
            var id = t.GetDocumentationCommentId() ?? AddressResolver.Render(t);
            if (seen.Add(id)) types.Add(t);
        }

        var sb = new StringBuilder();
        // Ambiguity signal: a bare name that rolls up to >1 type is a valid multi-hit read (exit 0,
        // all shown), but say so — otherwise the frames read as unrelated and the agent can't tell the
        // name is ambiguous. An edit by this bare name would exit 3; name the disambiguation up front.
        if (types.Count > 1)
            sb.AppendLine(
                $"# ambiguous '{address}' — {types.Count} types match; showing all. " +
                "Cite the namespace-qualified name to target one for an edit.");
        for (int i = 0; i < types.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            foreach (var line in FrameRenderer.FullType(types[i], host.Relativize, "inspect")) sb.AppendLine(line);
        }
        return Cs4AiResult.Ok(sb.ToString());
    }

    private static async Task<Cs4AiResult> InspectTraceAsync(
        SolutionHost host, Solution view, string fromFile, CancellationToken ct)
    {
        if (!File.Exists(fromFile)) return Cs4AiResult.FileError($"--from file not found: {fromFile}");
        var text = await File.ReadAllTextAsync(fromFile, ct);

        var sb = new StringBuilder();
        int resolved = 0, framework = 0, unresolved = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in StackFrameParser.Parse(text))
        {
            if (StackFrameParser.IsFrameworkFrame(frame.Fqn)) { framework++; continue; }
            var addr = frame.File is not null && frame.Line > 0 ? $"{frame.File}:{frame.Line}" : frame.Fqn;
            var (r, err) = await AddressResolver.ResolveAsync(view, addr, ct);
            if (err is not null || r.Symbol is null) { unresolved++; continue; }
            var type = r.Symbol as INamedTypeSymbol ?? r.Symbol.ContainingType;
            if (type is null) { unresolved++; continue; }
            var id = type.GetDocumentationCommentId() ?? AddressResolver.Render(type);
            if (!seen.Add(id)) { resolved++; continue; }
            foreach (var line in FrameRenderer.FullType(type, host.Relativize, "inspect")) sb.AppendLine(line);
            sb.AppendLine();
            resolved++;
        }
        sb.Append("# resolved ").Append(resolved).Append(" frame(s); ")
          .Append(framework).Append(" framework (collapsed); ").Append(unresolved).AppendLine(" unresolved");
        return Cs4AiResult.Ok(sb.ToString());
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  discover — breadth: census + directional references (incoming / outgoing).
    // ─────────────────────────────────────────────────────────────────────────────

    public static async Task<Cs4AiResult> RunDiscover(SolutionHost host, string[] rest, CancellationToken ct)
    {
        var p = ArgParse.Parse(rest);
        if (p.Help) return Cs4AiResult.Usage(Help.Discover);

        var sessToken = p.Positionals.Count > 0 ? p.Positionals[0] : null;
        var (view, viewErr) = await host.ResolveViewAsync(sessToken, ct);
        if (viewErr is { } ve) return ve;

        if (p.Positionals.Count < 2)
            return Cs4AiResult.UsageError("discover: usage: cs4ai discover <sess-token> <name-or-fqn>");
        var address = p.Positionals[1];

        var (matches, addrErr) = await AddressResolver.ResolveManyAsync(view!, address, ct);
        if (addrErr is { } ae) return ae;
        if (matches.Count == 0) return Cs4AiResult.NotFound($"address not found: '{address}'");

        var sb = new StringBuilder();

        // Census: counts by category, with the [N] bracket-count prefix tying each count to its list.
        var types = matches.Where(m => m is INamedTypeSymbol).ToList();
        var methods = matches.Where(m => m is IMethodSymbol).ToList();
        var others = matches.Where(m => m is not INamedTypeSymbol and not IMethodSymbol).ToList();
        sb.Append("# discover '").Append(address).AppendLine("'");
        AppendCategory(sb, "Types", types);
        AppendCategory(sb, "Methods", methods);
        AppendCategory(sb, "Other", others);

        // Directional references for the primary (unambiguous) hit.
        if (matches.Count == 1)
        {
            var sym = matches[0];
            sb.AppendLine();
            sb.Append("Referenced by:");
            var refs = await SymbolFinder.FindReferencesAsync(sym, view!, ct);
            var incoming = new List<string>();
            foreach (var rs in refs)
                foreach (var loc in rs.Locations)
                {
                    var ls = loc.Location.GetLineSpan();
                    incoming.Add($"  {host.Relativize(ls.Path)}:{ls.StartLinePosition.Line + 1}");
                }
            sb.Append(' ').Append('[').Append(incoming.Count).AppendLine("]");
            foreach (var line in incoming) sb.AppendLine(line);
            if (incoming.Count == 0) sb.AppendLine("  (none)");

            if (sym is IMethodSymbol method)
            {
                var outgoing = await OutgoingCallsAsync(view!, method, ct);
                sb.Append("References: [").Append(outgoing.Count).AppendLine("]");
                foreach (var o in outgoing) sb.Append("  ").AppendLine(o);
                if (outgoing.Count == 0) sb.AppendLine("  (none)");
            }
        }

        return Cs4AiResult.Ok(sb.ToString());
    }

    private static void AppendCategory(StringBuilder sb, string label, List<ISymbol> syms)
    {
        sb.Append(label).Append(": [").Append(syms.Count).AppendLine("]");
        foreach (var s in syms)
            sb.Append("  ").AppendLine(AddressResolver.RenderWithSignature(s));
    }

    private static async Task<List<string>> OutgoingCallsAsync(
        Solution solution, IMethodSymbol method, CancellationToken ct)
    {
        var outgoing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declRef in method.DeclaringSyntaxReferences)
        {
            var node = await declRef.GetSyntaxAsync(ct);
            var doc = solution.GetDocument(node.SyntaxTree);
            if (doc is null) continue;
            var model = await doc.GetSemanticModelAsync(ct);
            if (model is null) continue;
            foreach (var invoc in node.DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (model.GetSymbolInfo(invoc, ct).Symbol is IMethodSymbol called)
                    outgoing.Add(AddressResolver.Render(called));
        }
        return outgoing.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}
