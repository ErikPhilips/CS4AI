using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Compute a type's staleness token. **The hash uses raw source text — no
/// <c>NormalizeWhitespace</c>, no comment stripping, no formatter run on the hash input.**
/// Under AI-only writes + canonical formatting on every write, source text is deterministically
/// produced by cs4ai itself, so the hash doesn't need to be immune to formatting drift — drift
/// cannot happen. Same input always produces same output; that is all the hash needs to be.
/// <para>
/// See the design doc, "The recipe" subsection.
/// </para>
/// </summary>
internal static class TokenBuilder
{
    public static string CanonicalForm(INamedTypeSymbol type)
    {
        var sb = new StringBuilder();

        // 1. Stable identity.
        sb.Append("ID:").AppendLine(type.GetDocumentationCommentId() ?? "");

        // 2. Inheritance.
        sb.Append("BASE:").AppendLine(type.BaseType?.GetDocumentationCommentId() ?? "");
        foreach (var iface in type.Interfaces
                                  .OrderBy(i => i.GetDocumentationCommentId(), StringComparer.Ordinal))
            sb.Append("IFACE:").AppendLine(iface.GetDocumentationCommentId());

        // 3. Attributes on the type itself (ordered for determinism).
        foreach (var attr in type.GetAttributes().OrderBy(AttributeKey, StringComparer.Ordinal))
            sb.Append("ATTR:").AppendLine(AttributeKey(attr));

        // 4. Members. Ordered by DocId so cross-partial order is stable regardless of which file
        //    Roslyn read first.
        foreach (var member in type.GetMembers()
                                  .Where(m => !m.IsImplicitlyDeclared)
                                  .OrderBy(m => m.GetDocumentationCommentId(), StringComparer.Ordinal))
        {
            sb.Append("MEM:").AppendLine(member.GetDocumentationCommentId() ?? "");

            foreach (var declRef in member.DeclaringSyntaxReferences
                                          .OrderBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal))
            {
                var node = declRef.GetSyntax();
                sb.AppendLine(node.ToFullString());
            }
        }

        return sb.ToString();
    }

    private static string AttributeKey(AttributeData a)
    {
        var sb = new StringBuilder();
        sb.Append(a.AttributeClass?.GetDocumentationCommentId() ?? "");
        foreach (var arg in a.ConstructorArguments)
            sb.Append('|').Append(TypedConstantString(arg));
        foreach (var kv in a.NamedArguments.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append('|').Append(kv.Key).Append('=').Append(TypedConstantString(kv.Value));
        return sb.ToString();
    }

    /// <summary>Stable string for an attribute argument value. <see cref="TypedConstant.ToCSharpString"/>
    /// is internal in Roslyn; <see cref="TypedConstant.ToString"/> is the public equivalent and
    /// produces the same canonical text for our hashing purposes.</summary>
    private static string TypedConstantString(TypedConstant c) => c.ToString() ?? "";

    public static byte[] Token(INamedTypeSymbol type) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalForm(type)));

    /// <summary>The staleness token is an equality-only guard (see <see cref="CanonicalForm"/>),
    /// never reversed or used as a security boundary — so the full 256-bit hash is unnecessary.
    /// 48 bits (12 hex) is collision-proof for the handful of states a single type passes through
    /// in a work-span, and matches the pipe-key truncation in <c>DaemonProtocol</c>.</summary>
    public static string TokenString(INamedTypeSymbol type) =>
        Convert.ToHexString(Token(type))[..12].ToLowerInvariant();
}
