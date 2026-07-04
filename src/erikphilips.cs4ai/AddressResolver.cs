using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Parse and resolve cs4ai addresses against a loaded <see cref="Solution"/>. Three address
/// forms (the design doc's "Three address forms" subsection):
/// <list type="bullet">
///   <item>FQN — <c>Namespace.Type.Member</c>, with optional <c>(P1,P2)</c> overload signature.</item>
///   <item>File path — <c>src/Foo/Bar.cs</c> (relative or absolute).</item>
///   <item>file:line — <c>src/Foo/Bar.cs:42</c> resolves to the symbol containing line 42.</item>
/// </list>
/// <para>
/// Identity uses <c>DocumentationCommentId</c> internally (the design doc's "Three address forms"
/// subsection commits to DocId as both token identity and address identity). Input liberally
/// accepts angle brackets <c>&lt;T&gt;</c> or backticks <c>``1</c>; output renders the symbol's
/// canonical <c>SymbolDisplayFormat.FullyQualifiedFormat</c>.
/// </para>
/// <para>
/// Ambiguity returns the candidate list (exit 3). Never guesses.
/// </para>
/// </summary>
internal static class AddressResolver
{
    public readonly record struct Resolved(ISymbol? Symbol, IReadOnlyList<ISymbol> AllMatches);

    /// <summary>
    /// Strict resolution: exactly one symbol or an error. Writes and `usages` use this — a
    /// mutation needs an unambiguous target, so >1 match is exit 3 with the candidate list.
    /// </summary>
    public static async Task<(Resolved result, Cs4AiResult? error)> ResolveAsync(
        Solution solution, string address, CancellationToken ct = default)
    {
        var (matches, error) = await ResolveManyAsync(solution, address, ct);
        if (error is not null) return (default, error);

        return matches.Count switch
        {
            1 => (new Resolved(matches[0], matches), null),
            // Signatures in the candidate list: overloads (and ctors) render identically
            // without them, and an unrenderable ambiguity is undisambiguatable.
            _ => (default, Cs4AiResult.Ambiguous(
                    $"ambiguous address '{address}' — {matches.Count} candidates:\n" +
                    string.Join("\n", matches.Select(s => "  " + RenderWithSignature(s))))),
        };
    }

    /// <summary>
    /// Liberal resolution: every symbol the address matches. `inspect` uses this — for a read,
    /// multiple hits are an ANSWER (rendered at the count-sensitive depth default), not an
    /// error. Found in the field: an agent asked inspect for both candidates at
    /// `--depth signatures` and got exit 3 instead of the designed multi-hit view.
    /// </summary>
    public static async Task<(IReadOnlyList<ISymbol> matches, Cs4AiResult? error)> ResolveManyAsync(
        Solution solution, string address, CancellationToken ct = default)
    {
        address = address.Trim();
        if (address.Length == 0)
            return ([], Cs4AiResult.UsageError("empty address"));

        // file:line form: "path/Foo.cs:42" — inherently single-target.
        var fileLine = TryParseFileLine(address);
        if (fileLine.HasValue)
        {
            var (resolved, flErr) = await ResolveFileLineAsync(
                solution, fileLine.Value.path, fileLine.Value.line, ct);
            return flErr is not null ? ([], flErr) : (resolved.AllMatches, null);
        }

        // Bare file path form: ends with .cs and exists in the solution.
        if (address.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return ([], Cs4AiResult.UsageError(
                "bare file paths aren't directly addressable as a symbol; use 'path/Foo.cs:LINE' " +
                "to resolve the symbol at a line, or address a symbol by FQN."));

        // Otherwise: FQN (possibly with overload signature in parens, or generic via < > / ``).
        return await CollectFqnMatchesAsync(solution, address, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  file:line
    // ─────────────────────────────────────────────────────────────────────────────

    private static (string path, int line)? TryParseFileLine(string address)
    {
        // Match a trailing :digits, but only when the prefix looks like a path (contains .cs).
        var m = Regex.Match(address, @"^(.+\.cs):(\d+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return (m.Groups[1].Value, int.Parse(m.Groups[2].Value));
    }

    private static async Task<(Resolved, Cs4AiResult?)> ResolveFileLineAsync(
        Solution solution, string relPath, int line, CancellationToken ct)
    {
        // Find a Document whose file path ends with the supplied path (case-insensitive on Windows).
        var normalized = relPath.Replace('\\', '/');
        Document? doc = null;
        foreach (var project in solution.Projects)
        {
            foreach (var d in project.Documents)
            {
                var p = (d.FilePath ?? "").Replace('\\', '/');
                if (p.EndsWith(normalized, StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase))
                {
                    doc = d; break;
                }
            }
            if (doc is not null) break;
        }
        if (doc is null)
            return (default, Cs4AiResult.NotFound($"no document matched path '{relPath}'"));

        var tree = await doc.GetSyntaxTreeAsync(ct);
        var text = await doc.GetTextAsync(ct);
        if (tree is null || line < 1 || line > text.Lines.Count)
            return (default, Cs4AiResult.NotFound($"line {line} out of range in '{relPath}'"));

        // Line is 1-based; SourceText is 0-based.
        var lineSpan = text.Lines[line - 1].Span;
        var root = await tree.GetRootAsync(ct);
        var node = root.FindNode(lineSpan, getInnermostNodeForTie: true);

        var model = await doc.GetSemanticModelAsync(ct);
        if (model is null)
            return (default, Cs4AiResult.FileError($"no semantic model for '{doc.FilePath}'"));

        // Walk up until we hit a member-declaration-shaped node and grab its declared symbol.
        for (var n = node; n is not null; n = n.Parent)
        {
            if (n is MemberDeclarationSyntax member)
            {
                var sym = model.GetDeclaredSymbol(member, ct);
                if (sym is not null)
                    return (new Resolved(sym, [sym]), null);
            }
        }
        return (default, Cs4AiResult.NotFound($"no member declaration contains line {line} in '{relPath}'"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  FQN
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<(IReadOnlyList<ISymbol>, Cs4AiResult?)> CollectFqnMatchesAsync(
        Solution solution, string address, CancellationToken ct)
    {
        // Pull the signature off the RAW address first — it must stay in C# spelling for the
        // sig filter below (NormalizeGenerics would mangle e.g. `(List<int>)` into backtick form).
        // Two shapes: `Name(P1,P2)` for methods/ctors, `Type.this[P]` for indexers.
        string namePart = address;
        string? sigRaw = null;
        bool indexer = false;
        var paren = address.IndexOf('(');
        if (paren > 0 && address.EndsWith(")", StringComparison.Ordinal))
        {
            namePart = address[..paren].TrimEnd();
            sigRaw = address[(paren + 1)..^1];
        }
        else
        {
            var ix = Regex.Match(address, @"^(?<type>.+)\.this\[(?<sig>.*)\]$");
            if (ix.Success)
            {
                indexer = true;
                namePart = ix.Groups["type"].Value + ".this";
                sigRaw = ix.Groups["sig"].Value;
            }
        }

        // Liberal input: convert angle-bracket generics to backtick arity for matching.
        var nameOnly = NormalizeGenerics(namePart);
        var overloadSig = !indexer && sigRaw is not null ? $"({sigRaw})" : null;

        // Dedup across projects by DocumentationCommentId — the same source-defined symbol shows
        // up in every project that references its assembly, but we want to surface it once.
        var matchesById = new Dictionary<string, ISymbol>(StringComparer.Ordinal);
        var compilations = new List<Microsoft.CodeAnalysis.Compilation>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            compilations.Add(compilation);

            // Phase 1: exact-FQN lookup via DocumentationCommentId, across every project.
            foreach (var kind in new[] { "T", "M", "P", "F", "E" })
            {
                var docId = $"{kind}:{nameOnly}";
                if (overloadSig is not null) docId += overloadSig;
                var sym = DocumentationCommentId.GetFirstSymbolForDeclarationId(docId, compilation);
                if (sym is null) continue;

                // A sig-less method address that exact-hits here means "the PARAMETERLESS
                // overload" in DocId grammar — but the agent wrote a name. If siblings overload
                // it, surface them all so the ambiguity path forces a signature (never guess).
                var found = sigRaw is null && sym is IMethodSymbol { MethodKind: MethodKind.Ordinary } ms
                    ? ms.ContainingType.GetMembers(ms.Name).OfType<IMethodSymbol>()
                        .Where(o => o.MethodKind == MethodKind.Ordinary).Cast<ISymbol>()
                    : [sym];
                foreach (var s in found)
                {
                    var symId = s.GetDocumentationCommentId();
                    if (symId is not null && !matchesById.ContainsKey(symId))
                        matchesById[symId] = s;
                }
            }
        }

        // Phase 2 (only when no exact FQN hit anywhere): suffix-scan EVERY project's own source
        // assembly. The scan must not stop at the first project that matches — a bare name like
        // `AddCQRS` can be defined independently in several projects, and returning only the
        // first would be a confidently incomplete answer (found in the field: the OpenSearch
        // twin of an EF extension method was silently dropped). Scanning
        // `compilation.Assembly.GlobalNamespace` (not `compilation.GlobalNamespace`) keeps each
        // pass to that project's source symbols instead of re-walking the whole BCL per project.
        if (matchesById.Count == 0)
        {
            foreach (var compilation in compilations)
            {
                var found = new List<ISymbol>();
                CollectSuffixMatches(compilation.Assembly.GlobalNamespace, nameOnly, found);
                foreach (var sym in found)
                {
                    var symId = sym.GetDocumentationCommentId();
                    if (symId is not null && !matchesById.ContainsKey(symId))
                        matchesById[symId] = sym;
                }
            }
        }
        var matches = matchesById.Values.ToList();

        // Signature filter — the DocId phase only understands fully-qualified metadata sigs, so a
        // C#-spelled `(int,string)` (or an indexer's `[int]`) lands here with every same-name
        // overload collected; without this filter the sig would be decorative.
        if (sigRaw is not null && matches.Count > 0)
        {
            var filtered = matches.Where(m => SigMatches(m, sigRaw)).ToList();
            if (filtered.Count == 0)
                return ([], Cs4AiResult.NotFound(
                    $"no overload of '{address}' matches — candidates:\n" +
                    string.Join("\n", matches.Select(s => "  " + RenderWithSignature(s)))));
            matches = filtered;
        }

        return matches.Count == 0
            ? ([], Cs4AiResult.NotFound($"address not found: '{address}'"))
            : (matches, null);
    }

    /// <summary>Compare a C#-spelled parameter list against a symbol's parameters, in both the
    /// minimal spelling (<c>int</c>, <c>List&lt;int&gt;</c>) and the fully-qualified one
    /// (<c>System.Int32</c>), whitespace-insensitive. An empty sig means "parameterless".</summary>
    private static bool SigMatches(ISymbol symbol, string sigRaw)
    {
        var ps = symbol switch
        {
            IMethodSymbol m => m.Parameters,
            IPropertySymbol { IsIndexer: true } p => p.Parameters,
            _ => default,
        };
        if (ps.IsDefault) return false; // sig given, but the symbol has no parameter list at all

        var wanted = StripSpaces(sigRaw);
        return wanted == StripSpaces(string.Join(",", ps.Select(p => p.Type.ToDisplayString(SigMinimalFormat))))
            || wanted == StripSpaces(string.Join(",", ps.Select(p => p.Type.ToDisplayString(SigFullFormat))));

        static string StripSpaces(string s) => s.Replace(" ", "").Replace("\t", "");
    }

    private static readonly SymbolDisplayFormat SigMinimalFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;
    private static readonly SymbolDisplayFormat SigFullFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>Convert <c>IRepository&lt;T&gt;</c> → <c>IRepository`1</c>; leave existing
    /// backticks alone.</summary>
    private static string NormalizeGenerics(string s)
    {
        // Replace each balanced &lt;...&gt; with the backtick form `N where N counts top-level commas + 1.
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '<')
            {
                int depth = 1, j = i + 1, commas = 0;
                while (j < s.Length && depth > 0)
                {
                    if (s[j] == '<') depth++;
                    else if (s[j] == '>') depth--;
                    else if (s[j] == ',' && depth == 1) commas++;
                    if (depth > 0) j++;
                }
                if (depth == 0)
                {
                    sb.Append('`').Append(commas + 1);
                    i = j + 1;
                    continue;
                }
            }
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Boundary-aware suffix match: the suffix must align with the start of a name segment, so
    /// `QueryEngine` matches `Ns.QueryEngine` and `Ns.QueryEngine&lt;T&gt;` but NOT
    /// `IQueryEngine` or `AddQueryEngine`. Compared against both the rendered FQN and the
    /// arity-free metadata-name path, so bare names find generic types.
    /// </summary>
    private static bool SuffixMatches(ISymbol symbol, string suffix)
    {
        return AlignsAtBoundary(FullName(symbol), suffix)
            || AlignsAtBoundary(MetadataPath(symbol, arity: false), suffix)
            || AlignsAtBoundary(MetadataPath(symbol, arity: true), suffix);

        static bool AlignsAtBoundary(string full, string suffix)
        {
            if (!full.EndsWith(suffix, StringComparison.Ordinal)) return false;
            if (full.Length == suffix.Length) return true;
            var preceding = full[full.Length - suffix.Length - 1];
            return preceding == '.' || preceding == '+';
        }
    }

    /// <summary>True when the suffix explicitly names a constructor in C# spelling: its last
    /// segment is the type's own name AND the rest of the suffix names the type — i.e.
    /// `Repo.Repo` / `Ns.Repo.Repo`, never a bare `Repo`.</summary>
    private static bool CtorAddressed(string suffix, INamedTypeSymbol type)
    {
        var lastDot = suffix.LastIndexOf('.');
        if (lastDot <= 0) return false; // a bare name always means the type, never its ctor

        var lastSegment = suffix[(lastDot + 1)..];
        var tick = lastSegment.IndexOf('`');
        if (tick >= 0) lastSegment = lastSegment[..tick];

        return string.Equals(lastSegment, type.Name, StringComparison.Ordinal)
            && SuffixMatches(type, suffix[..lastDot]);
    }

    /// <summary>Dotted path of plain symbol names: `Ns.Sub.QueryEngine` for
    /// `Ns.Sub.QueryEngine&lt;T&gt;` — or, with <paramref name="arity"/>, the metadata form
    /// `Ns.Sub.QueryEngine`1` so backtick-arity input (`QueryEngine&lt;&gt;` normalized,
    /// `QueryEngine`1` verbatim) also resolves.</summary>
    private static string MetadataPath(ISymbol symbol, bool arity)
    {
        var parts = new Stack<string>();
        for (var s = symbol; s is not null && s.Name.Length > 0; s = s.ContainingSymbol)
        {
            if (s is INamespaceSymbol { IsGlobalNamespace: true }) break;
            parts.Push(arity ? s.MetadataName : s.Name);
        }
        return string.Join('.', parts);
    }

    private static void CollectSuffixMatches(INamespaceOrTypeSymbol container, string suffix, List<ISymbol> sink)
    {
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns)
                CollectSuffixMatches(ns, suffix, sink);
            else if (member is INamedTypeSymbol type)
            {
                if (SuffixMatches(type, suffix))
                    sink.Add(type);
                foreach (var m in type.GetMembers())
                {
                    if (m.IsImplicitlyDeclared) continue;
                    // Constructors: addressable ONLY by the explicit C# spelling `…Type.Type`
                    // (doubled name). A bare-suffix match would shadow the type itself in
                    // ambiguity reports — which is why they're excluded from normal matching.
                    if (m is IMethodSymbol meth && meth.MethodKind == MethodKind.Constructor)
                    {
                        if (CtorAddressed(suffix, type)) sink.Add(m);
                        continue;
                    }
                    // Skip property accessors, event accessors — not separately addressable.
                    if (m is IMethodSymbol m2 && m2.MethodKind is
                            MethodKind.PropertyGet or MethodKind.PropertySet
                            or MethodKind.EventAdd or MethodKind.EventRemove
                            or MethodKind.EventRaise) continue;
                    // Indexers: addressed as `…Type.this` (the `[sig]` was already peeled off).
                    if (m is IPropertySymbol { IsIndexer: true })
                    {
                        if (suffix.EndsWith(".this", StringComparison.Ordinal)
                            && SuffixMatches(type, suffix[..^".this".Length]))
                            sink.Add(m);
                        continue;
                    }
                    if (SuffixMatches(m, suffix))
                        sink.Add(m);
                }
                // Recurse into nested types.
                CollectSuffixMatches(type, suffix, sink);
            }
        }
    }

    private static readonly SymbolDisplayFormat FqnFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
            .WithMemberOptions(SymbolDisplayMemberOptions.IncludeContainingType);

    private static string FullName(ISymbol s) => s.ToDisplayString(FqnFormat);

    public static string Render(ISymbol s) => FullName(s);

    public static string RenderWithSignature(ISymbol s) =>
        s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
            .WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters
                            | SymbolDisplayMemberOptions.IncludeType
                            | SymbolDisplayMemberOptions.IncludeContainingType)
            .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType));
}
