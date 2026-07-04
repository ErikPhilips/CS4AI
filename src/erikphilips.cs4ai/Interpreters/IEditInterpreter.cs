namespace ErikPhilips.Cs4Ai;

/// <summary>
/// One per-command interpreter: maps a verb's argv slice (everything after <c>cs4ai &lt;verb&gt;</c>)
/// to a ready-to-send <see cref="SemanticOperation"/>. Each interpreter owns <i>its own</i> flag set,
/// so an unknown flag is caught here (exit 1) without the engine ever seeing argv. It validates only
/// that argv <i>maps</i> — all <i>operation</i> validation is engine-side (<see cref="OperationGrammar"/>).
/// <para>
/// Plugin-reachable: a plugin can call an interpreter to turn argv-like input into a
/// <see cref="SemanticOperation"/>, or skip it and build a typed command + call the matching
/// <c>SemanticOperation.From*</c> factory directly. Two callers, one downstream grammar.
/// </para>
/// </summary>
internal interface IEditInterpreter
{
    /// <summary>The op this interpreter serves (one of <see cref="Ops"/>).</summary>
    string Op { get; }

    /// <summary>
    /// Parse <paramref name="args"/> (the slice after the verb: <c>&lt;sess-token&gt; &lt;positionals&gt;
    /// [flags]</c>) into a <see cref="SemanticOperation"/>, or return an exit-1/-2 error. Async because
    /// a body may be sourced from <c>--from &lt;file&gt;</c> or stdin.
    /// </summary>
    Task<(SemanticOperation? op, Cs4AiResult? error)> ParseAsync(
        string[] args, TextReader? stdin, CancellationToken ct);
}

/// <summary>
/// Shared parsing mechanics for the interpreters: session-token extraction with per-slot prefix
/// validation (version2.md, <i>Tokens</i>), grammar-driven unknown-flag rejection, and body-content
/// resolution (inline <c>--set-body</c> or the <c>--from</c>/<c>--text</c>/stdin content channel).
/// </summary>
internal static class InterpreterSupport
{
    public const string SessionPrefix = "sess_";
    public const string TypeTokenPrefix = "type_";

    /// <summary>
    /// Pull the session token (the first positional after the verb) and validate its <c>sess_</c>
    /// prefix — a <c>type_</c> token in the session slot is an immediate error, never a silent
    /// re-route.
    /// </summary>
    public static (string? session, Cs4AiResult? error) TakeSessionToken(ArgParse p, string op)
    {
        if (p.Positionals.Count == 0)
            return (null, Cs4AiResult.UsageError(
                $"{op}: missing session token — usage: cs4ai {op} <sess-token> …"));

        var token = p.Positionals[0];
        if (token.StartsWith(TypeTokenPrefix, StringComparison.Ordinal))
            return (null, Cs4AiResult.UsageError(
                $"{op}: '{token}' is a type token ({TypeTokenPrefix}…) but the session slot expects a " +
                $"session token ({SessionPrefix}…). Lead with the session token; cite --token for staleness."));
        if (!token.StartsWith(SessionPrefix, StringComparison.Ordinal))
            return (null, Cs4AiResult.UsageError(
                $"{op}: expected a session token ({SessionPrefix}…) as the first argument, got '{token}'."));

        return (token, null);
    }

    /// <summary>Validate the cited staleness token's <c>type_</c> prefix (a <c>sess_</c> in the
    /// <c>--token</c> slot is an immediate error). Null token is fine — the engine handles a missing
    /// token as the stale path (exit 5).</summary>
    public static Cs4AiResult? ValidateTypeToken(string? token, string op)
    {
        if (token is null) return null;
        if (token.StartsWith(SessionPrefix, StringComparison.Ordinal))
            return Cs4AiResult.UsageError(
                $"{op}: --token got a session token ({SessionPrefix}…); it expects a type token " +
                $"({TypeTokenPrefix}…). Pass the session token as the leading positional instead.");
        if (!token.StartsWith(TypeTokenPrefix, StringComparison.Ordinal))
            return Cs4AiResult.UsageError(
                $"{op}: --token expects a type token ({TypeTokenPrefix}…), got '{token}'.");
        return null;
    }

    /// <summary>
    /// Reject any flag not in this op's allowed set (from <see cref="OperationGrammar.AllowedFlags"/>
    /// ∪ {<c>--token</c>}). Value-flag values are skipped so a body like <c>"--&gt; x"</c> isn't
    /// mistaken for a flag. The interpreter is a strict argv→bag mapper: an unknown flag is exit 1,
    /// never silent-dropped (a hallucinated flag from old/other-tool priors must fail loudly).
    /// </summary>
    public static Cs4AiResult? RejectUnknownFlags(string[] args, string op)
    {
        var allowed = new HashSet<string>(OperationGrammar.AllowedFlags(op), StringComparer.Ordinal)
        {
            "--token",
        };

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            bool isFlag = a.StartsWith('-') && a is not "-h" and not "--help";
            if (isFlag && !allowed.Contains(a))
                return Cs4AiResult.UsageError(
                    $"{op}: unknown flag '{a}'. Allowed: {string.Join(", ", allowed.OrderBy(x => x, StringComparer.Ordinal))}.");
            if (ArgParse.ValueFlags.Contains(a) && i + 1 < args.Length)
                i++; // skip the flag's value
        }
        return null;
    }

    /// <summary>Body content: the inline <c>--set-body</c> value, else the content channel
    /// (<c>--from</c>/<c>--text</c>/stdin). Null when none supplied (the grammar then rejects if Body
    /// is required).</summary>
    public static async Task<(string? body, Cs4AiResult? error)> ResolveBodyAsync(
        ArgParse p, TextReader? stdin)
    {
        if (p.SetBody is not null) return (p.SetBody, null);
        var (text, err) = await p.ReadContentAsync(stdin);
        return (text, err);
    }
}
