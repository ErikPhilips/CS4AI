using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Canonical class formatting per the repo's `.cs4aiconfig`. Strips `#region` directives, sorts
/// members by config (kind → access → name, with the <c>[StructLayout(LayoutKind.Sequential)]</c>
/// carve-out preserving field source order), then formats with Roslyn's
/// <see cref="Formatter.Format(SyntaxNode, Workspace, OptionSet?)"/>.
/// See the design doc's "Canonical Formatting" section.
/// </summary>
internal static class Canonicalizer
{
    /// <summary>Apply canonical formatting to the type declaration. Returns the rewritten
    /// declaration. Caller is responsible for splicing it back into the document tree.</summary>
    public static TypeDeclarationSyntax Apply(
        TypeDeclarationSyntax type, Workspace workspace, Cs4AiConfig config)
    {
        if (config.Canonicalize == "off") return StripRegions(type);

        var updated = StripRegions(type);

        if (config.Canonicalize is "full" or "members")
            updated = updated.WithMembers(SyntaxFactory.List(
                SeparateMembers(SortByCanonicalOrder(updated.Members, updated, config))));

        if (config.Canonicalize is "full" or "format")
            updated = (TypeDeclarationSyntax)Formatter.Format(updated, workspace);

        return updated;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Region stripping — unconditional. The design doc settled this as item 17.
    // ─────────────────────────────────────────────────────────────────────────────

    private static TypeDeclarationSyntax StripRegions(TypeDeclarationSyntax type)
    {
        var trivia = type.DescendantTrivia(descendIntoTrivia: true)
            .Where(t => t.IsKind(SyntaxKind.RegionDirectiveTrivia)
                     || t.IsKind(SyntaxKind.EndRegionDirectiveTrivia))
            .ToList();
        if (trivia.Count == 0) return type;

        return type.ReplaceTrivia(trivia, (_, _) => default);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Member ordering — config-driven, with the [StructLayout(Sequential)] carve-out.
    // ─────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<MemberDeclarationSyntax> SortByCanonicalOrder(
        SyntaxList<MemberDeclarationSyntax> members, TypeDeclarationSyntax type, Cs4AiConfig config)
    {
        bool layoutSensitive = HasStructLayoutSequential(type);

        return members
            .Select((m, originalIndex) => (m, originalIndex))
            .OrderBy(t => KindIndex(t.m, config))
            .ThenBy(t => AccessIndex(t.m, config))
            .ThenBy(t => layoutSensitive && t.m is FieldDeclarationSyntax
                ? t.originalIndex.ToString("D10")   // source-order key for fields on layout-sensitive types
                : NameOf(t.m), StringComparer.Ordinal)
            .Select(t => t.m);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Member separation — a sorted-in member (parsed from --set-body) carries no
    //  trailing newline, and the member that now follows it carries no leading one
    //  (that newline belonged to its old predecessor's trailing trivia), so the pair
    //  renders glued on one line. Formatter.Format normalizes spacing but never adds
    //  line breaks. Pairs already on separate lines are left untouched.
    // ─────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<MemberDeclarationSyntax> SeparateMembers(
        IEnumerable<MemberDeclarationSyntax> members)
    {
        MemberDeclarationSyntax? prev = null;
        foreach (var member in members)
        {
            var cur = member;
            if (prev is not null
                && !HasEndOfLine(prev.GetTrailingTrivia())
                && !HasEndOfLine(cur.GetLeadingTrivia()))
            {
                // Line break + blank line, prepended so doc comments stay attached to
                // their member; the elastic trivia lets Formatter.Format settle indent.
                cur = cur.WithLeadingTrivia(SyntaxFactory.TriviaList(
                        SyntaxFactory.ElasticCarriageReturnLineFeed,
                        SyntaxFactory.ElasticCarriageReturnLineFeed)
                    .AddRange(cur.GetLeadingTrivia()));
            }
            yield return cur;
            prev = cur;
        }
    }

    private static bool HasEndOfLine(SyntaxTriviaList trivia) =>
        trivia.Any(t => t.IsKind(SyntaxKind.EndOfLineTrivia));

    private static int KindIndex(MemberDeclarationSyntax m, Cs4AiConfig config)
    {
        var name = KindNameOf(m);
        int idx = config.MemberOrder.IndexOf(name);
        return idx < 0 ? int.MaxValue : idx;
    }

    private static int AccessIndex(MemberDeclarationSyntax m, Cs4AiConfig config)
    {
        var name = AccessNameOf(m);
        int idx = config.AccessOrder.IndexOf(name);
        return idx < 0 ? int.MaxValue : idx;
    }

    private static string KindNameOf(MemberDeclarationSyntax m) => m switch
    {
        FieldDeclarationSyntax f when IsConst(f)  => "const",
        FieldDeclarationSyntax f when IsStatic(f) => "field-static",
        FieldDeclarationSyntax                    => "field-instance",
        ConstructorDeclarationSyntax              => "constructor",
        DestructorDeclarationSyntax               => "destructor",
        DelegateDeclarationSyntax                 => "delegate",
        EventDeclarationSyntax                    => "event",
        EventFieldDeclarationSyntax               => "event",
        EnumDeclarationSyntax                     => "enum",
        InterfaceDeclarationSyntax                => "interface",
        PropertyDeclarationSyntax                 => "property",
        IndexerDeclarationSyntax                  => "indexer",
        MethodDeclarationSyntax                   => "method",
        StructDeclarationSyntax                   => "nested-struct",
        ClassDeclarationSyntax                    => "nested-class",
        RecordDeclarationSyntax r when r.IsKind(SyntaxKind.RecordStructDeclaration) => "nested-record",
        RecordDeclarationSyntax                   => "nested-record",
        _ => "other",
    };

    private static string AccessNameOf(MemberDeclarationSyntax m)
    {
        var mods = m.Modifiers;
        bool pub  = mods.Any(SyntaxKind.PublicKeyword);
        bool prot = mods.Any(SyntaxKind.ProtectedKeyword);
        bool intl = mods.Any(SyntaxKind.InternalKeyword);
        bool priv = mods.Any(SyntaxKind.PrivateKeyword);
        return (pub, prot, intl, priv) switch
        {
            (true,  _,    _,    _)    => "public",
            (_,     true, true, _)    => "protected-internal",
            (_,     true, _,    true) => "private-protected",
            (_,     true, _,    _)    => "protected",
            (_,     _,    true, _)    => "internal",
            (_,     _,    _,    true) => "private",
            _                         => "private", // default member access in classes
        };
    }

    private static bool IsConst(FieldDeclarationSyntax f)  => f.Modifiers.Any(SyntaxKind.ConstKeyword);
    private static bool IsStatic(FieldDeclarationSyntax f) => f.Modifiers.Any(SyntaxKind.StaticKeyword);

    private static string NameOf(MemberDeclarationSyntax m) => m switch
    {
        FieldDeclarationSyntax f       => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "",
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        DestructorDeclarationSyntax d  => d.Identifier.Text,
        MethodDeclarationSyntax meth   => meth.Identifier.Text,
        PropertyDeclarationSyntax prop => prop.Identifier.Text,
        IndexerDeclarationSyntax       => "this",
        EventDeclarationSyntax ev      => ev.Identifier.Text,
        EventFieldDeclarationSyntax ef => ef.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "",
        DelegateDeclarationSyntax dl   => dl.Identifier.Text,
        BaseTypeDeclarationSyntax t    => t.Identifier.Text,
        _ => "",
    };

    // ─────────────────────────────────────────────────────────────────────────────
    //  [StructLayout(LayoutKind.Sequential)] detection.
    // ─────────────────────────────────────────────────────────────────────────────

    private static bool HasStructLayoutSequential(TypeDeclarationSyntax type)
    {
        foreach (var list in type.AttributeLists)
        foreach (var attr in list.Attributes)
        {
            var name = attr.Name.ToString();
            if (!name.EndsWith("StructLayout", StringComparison.Ordinal)) continue;
            if (attr.ArgumentList is not { } argList) continue;
            var first = argList.Arguments.FirstOrDefault();
            var firstText = first?.Expression.ToString() ?? "";
            if (firstText.EndsWith("Sequential", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
