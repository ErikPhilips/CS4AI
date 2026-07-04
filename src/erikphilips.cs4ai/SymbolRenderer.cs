using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Render symbols and types for response bodies. Three depths corresponding to the
/// <c>--depth</c> flag on <c>inspect</c>:
/// <list type="bullet">
///   <item><see cref="Addresses"/> — FQN list only (for disambiguation).</item>
///   <item><see cref="Signatures"/> — FQN + signature per member, no bodies.</item>
///   <item><see cref="Full"/> — FQN + full member source.</item>
/// </list>
/// </summary>
internal static class SymbolRenderer
{
    public enum Depth { Addresses, Signatures, Full }

    /// <summary>
    /// Pick the default depth based on the result count. Single hit → full; multi-hit → signatures.
    /// See the design doc's "Reads — collapse aggressively" subsection.
    /// </summary>
    public static Depth DefaultDepthFor(int resultCount) =>
        resultCount == 1 ? Depth.Full : Depth.Signatures;

    public static string Render(ISymbol symbol, Depth depth)
    {
        var sb = new StringBuilder();
        switch (depth)
        {
            case Depth.Addresses:
                sb.AppendLine(AddressResolver.Render(symbol));
                break;
            case Depth.Signatures:
                if (symbol is INamedTypeSymbol type)
                    RenderTypeSignatures(sb, type);
                else
                {
                    // A standalone member line must carry its containing type — in a multi-hit
                    // view, bare signatures are indistinguishable and not re-citable
                    // (output-then-cite: every line is an address you can copy back).
                    sb.AppendLine(AddressResolver.RenderWithSignature(symbol));
                }
                break;
            case Depth.Full:
                RenderFull(sb, symbol);
                break;
        }
        return sb.ToString();
    }

    public static string RenderAll(IReadOnlyList<ISymbol> symbols, Depth depth)
    {
        // At Signatures depth (the multi-hit default) coalesce member hits that share a containing
        // type into one group: type header + file(s) + token printed once, the matched members
        // listed beneath. This makes the type's staleness token readable from the FIRST read in the
        // ambiguous case — the case that otherwise forces a write into the exit-7/exit-5 token-
        // harvest round-trips (found in the field: renaming two same-named extension methods cost a
        // no_session and a stale refusal just to obtain each token) — without ever duplicating a
        // token or file across sibling hits.
        if (depth == Depth.Signatures)
            return RenderSignaturesGrouped(symbols);

        var sb = new StringBuilder();
        for (int i = 0; i < symbols.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append(Render(symbols[i], depth));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Group member hits by containing type so a multi-hit signatures view prints each type's
    /// file(s) and staleness token exactly once, with the matched members listed beneath it. A
    /// directly-hit type is its own group (rendered with its full member list via
    /// <see cref="RenderTypeSignatures"/>). A member with no containing type (rare) falls back to a
    /// bare signature line. Groups emit in first-seen order.
    /// </summary>
    private static string RenderSignaturesGrouped(IReadOnlyList<ISymbol> symbols)
    {
        var order = new List<string>();
        var typeHits = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
        var memberBuckets =
            new Dictionary<string, (INamedTypeSymbol type, List<ISymbol> members)>(StringComparer.Ordinal);
        var loose = new List<ISymbol>();

        foreach (var sym in symbols)
        {
            if (sym is INamedTypeSymbol type)
            {
                var key = "T:" + (type.GetDocumentationCommentId() ?? AddressResolver.Render(type));
                if (typeHits.TryAdd(key, type)) order.Add(key);
            }
            else if (sym.ContainingType is { } ct)
            {
                var key = "G:" + (ct.GetDocumentationCommentId() ?? AddressResolver.Render(ct));
                if (!memberBuckets.TryGetValue(key, out var bucket))
                {
                    bucket = (ct, new List<ISymbol>());
                    memberBuckets[key] = bucket;
                    order.Add(key);
                }
                bucket.members.Add(sym);
            }
            else
            {
                loose.Add(sym);
            }
        }

        var sb = new StringBuilder();
        bool first = true;
        foreach (var key in order)
        {
            if (!first) sb.AppendLine();
            first = false;

            if (typeHits.TryGetValue(key, out var type))
                RenderTypeSignatures(sb, type);
            else if (memberBuckets.TryGetValue(key, out var bucket))
                RenderMemberGroup(sb, bucket.type, bucket.members);
        }

        foreach (var sym in loose)
        {
            if (!first) sb.AppendLine();
            first = false;
            sb.AppendLine(AddressResolver.RenderWithSignature(sym));
        }

        return sb.ToString();
    }

    private static void RenderMemberGroup(StringBuilder sb, INamedTypeSymbol type, List<ISymbol> members)
    {
        sb.Append("# ").Append(KindKeyword(type)).Append(' ')
          .AppendLine(AddressResolver.Render(type));

        // File(s) where the matched members actually live — for a partial type that's the specific
        // partial(s) holding the hits, not every declaration file of the type.
        var files = members
            .SelectMany(m => m.DeclaringSyntaxReferences)
            .Select(r => r.SyntaxTree.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal);
        foreach (var f in files) sb.Append("# file: ").AppendLine(f);

        // The write-routing staleness token is the containing type's — emit it once for the group.
        sb.Append("# token: ").AppendLine(TokenBuilder.TokenString(type));

        foreach (var m in members)
            sb.Append("  ").AppendLine(RenderMemberSignature(m));
    }

    private static void RenderTypeSignatures(StringBuilder sb, INamedTypeSymbol type)
    {
        sb.Append("# ").Append(KindKeyword(type)).Append(' ')
          .AppendLine(AddressResolver.Render(type));

        var members = type.GetMembers()
                          .Where(m => !m.IsImplicitlyDeclared
                                   && m.DeclaredAccessibility != Accessibility.NotApplicable)
                          .OrderBy(m => m.Name, StringComparer.Ordinal);
        foreach (var m in members)
        {
            sb.Append("  ").AppendLine(RenderMemberSignature(m));
        }

        sb.AppendLine();
        sb.Append("# token: ").AppendLine(TokenBuilder.TokenString(type));
    }

    private static void RenderFull(StringBuilder sb, ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol type)
        {
            sb.Append("# ").Append(KindKeyword(type)).Append(' ')
              .AppendLine(AddressResolver.Render(type));
            sb.Append("# token: ").AppendLine(TokenBuilder.TokenString(type));
            sb.AppendLine();

            foreach (var declRef in type.DeclaringSyntaxReferences
                                        .OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal))
            {
                sb.Append("# file: ").AppendLine(declRef.SyntaxTree.FilePath);
                sb.AppendLine(declRef.GetSyntax().ToFullString());
            }

            // Partial-type shape: list per-file members so callers know which file to target.
            if (type.DeclaringSyntaxReferences.Length > 1)
            {
                sb.AppendLine();
                sb.AppendLine("# partial-files:");
                foreach (var declRef in type.DeclaringSyntaxReferences
                                            .OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal))
                {
                    var path = declRef.SyntaxTree.FilePath;
                    var node = declRef.GetSyntax();
                    var members = node is TypeDeclarationSyntax t
                        ? t.Members.Select(m => MemberShortName(m)).ToArray()
                        : [];
                    sb.Append("  ").Append(path).Append(": [").Append(string.Join(", ", members)).AppendLine("]");
                }
            }
        }
        else
        {
            // Single member: signature + body.
            sb.Append("# ").AppendLine(RenderMemberSignature(symbol));
            if (symbol.ContainingType is { } containing)
                sb.Append("# of: ").AppendLine(AddressResolver.Render(containing));
            sb.AppendLine();
            foreach (var declRef in symbol.DeclaringSyntaxReferences
                                          .OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal))
            {
                sb.Append("# file: ").AppendLine(declRef.SyntaxTree.FilePath);
                sb.AppendLine(declRef.GetSyntax().ToFullString());
            }
            if (symbol.ContainingType is { } owner)
            {
                sb.Append("# token (containing type): ").AppendLine(TokenBuilder.TokenString(owner));
            }
        }
    }

    private static string RenderMemberSignature(ISymbol s) =>
        s.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat
            .WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters
                            | SymbolDisplayMemberOptions.IncludeType
                            | SymbolDisplayMemberOptions.IncludeAccessibility
                            | SymbolDisplayMemberOptions.IncludeModifiers)
            .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType
                               | SymbolDisplayParameterOptions.IncludeName
                               | SymbolDisplayParameterOptions.IncludeParamsRefOut)
            .WithKindOptions(SymbolDisplayKindOptions.None));

    private static string MemberShortName(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax meth => meth.Identifier.Text,
        PropertyDeclarationSyntax prop => prop.Identifier.Text,
        FieldDeclarationSyntax field => string.Join(",", field.Declaration.Variables.Select(v => v.Identifier.Text)),
        ConstructorDeclarationSyntax => ".ctor",
        EventDeclarationSyntax ev => ev.Identifier.Text,
        EventFieldDeclarationSyntax ef => string.Join(",", ef.Declaration.Variables.Select(v => v.Identifier.Text)),
        BaseTypeDeclarationSyntax t => t.Identifier.Text,
        DelegateDeclarationSyntax d => d.Identifier.Text,
        _ => m.Kind().ToString(),
    };

    public static string KindKeyword(INamedTypeSymbol t) => t.TypeKind switch
    {
        TypeKind.Class     => t.IsRecord ? "record" : "class",
        TypeKind.Struct    => t.IsRecord ? "record struct" : "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum      => "enum",
        TypeKind.Delegate  => "delegate",
        _ => t.TypeKind.ToString().ToLowerInvariant(),
    };
}
