using System.Text.RegularExpressions;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Parses .NET stack frames so <c>inspect</c>'s address grammar can eat whatever a runtime hands the
/// agent (version2.md, §12 — "if inspect eats a raw stack frame, it eats anything"). Extracted from
/// v1's <c>trace</c> verb, which folds into <c>inspect</c>. Strips the <c>at </c> prefix and trailing
/// IL decoration (<c>+92</c>), and prefers a <c>file:line</c> hint when present (most precise).
/// </summary>
internal static class StackFrameParser
{
    private static readonly Regex FrameRegex = new(
        @"^\s*at\s+(?<sig>[\w\.<>`,\s\[\]]+\([^)]*\))(?:\s+in\s+(?<file>[^:]+):(?:line\s+)?(?<line>\d+))?",
        RegexOptions.Compiled);

    public readonly record struct Frame(string Sig, string Fqn, string? File, int Line);

    /// <summary>Parse every recognizable frame from a multi-line trace.</summary>
    public static IEnumerable<Frame> Parse(string traceText)
    {
        foreach (var raw in traceText.Split('\n'))
        {
            var m = FrameRegex.Match(raw);
            if (!m.Success) continue;
            var sig = m.Groups["sig"].Value.Trim();
            var paren = sig.IndexOf('(');
            var fqn = paren > 0 ? sig[..paren] : sig;
            var file = m.Groups["file"].Success ? m.Groups["file"].Value.Trim() : null;
            var line = m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : 0;
            yield return new Frame(sig, fqn, file, line);
        }
    }

    /// <summary>
    /// Best resolvable address for a single line that may be a raw frame: a <c>file:line</c> hint if
    /// present (most precise), else the signature with trailing IL decoration stripped. Returns the
    /// input unchanged when it isn't frame-shaped (a plain FQN flows straight through to the resolver).
    /// </summary>
    public static string CleanAddress(string raw)
    {
        var m = FrameRegex.Match(raw);
        if (!m.Success)
        {
            // Not a full frame, but still strip a trailing "+92" IL offset if present.
            var plus = raw.LastIndexOf('+');
            return plus > 0 && raw[(plus + 1)..].Trim().All(char.IsDigit) ? raw[..plus].Trim() : raw.Trim();
        }
        if (m.Groups["file"].Success && m.Groups["line"].Success)
            return $"{m.Groups["file"].Value.Trim()}:{m.Groups["line"].Value}";
        return m.Groups["sig"].Value.Trim();
    }

    public static bool IsFrameworkFrame(string fqn) =>
        fqn.StartsWith("System.", StringComparison.Ordinal) ||
        fqn.StartsWith("Microsoft.", StringComparison.Ordinal) ||
        fqn.StartsWith("Castle.", StringComparison.Ordinal) ||
        fqn.StartsWith("Xunit.", StringComparison.Ordinal) ||
        fqn.StartsWith("MS.", StringComparison.Ordinal);
}
