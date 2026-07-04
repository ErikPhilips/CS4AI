using Microsoft.CodeAnalysis;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The <b>plugin read facade</b>: resolve and read against the warm <see cref="Compilation"/> —
/// nothing that can mutate. A plugin gets symbols and tokens, never a <see cref="Solution"/> it
/// could edit out from under the session.
/// </summary>
internal interface IHostQuery
{
    /// <summary>Strict resolution (exactly one symbol, or exit 3 ambiguity / exit 4 not-found) —
    /// for ops that need an unambiguous target.</summary>
    Task<(AddressResolver.Resolved result, Cs4AiResult? error)> ResolveAsync(
        string address, CancellationToken ct);

    /// <summary>Liberal resolution (every match) — for reads, where N hits are an answer.</summary>
    Task<(IReadOnlyList<ISymbol> matches, Cs4AiResult? error)> ResolveManyAsync(
        string address, CancellationToken ct);

    /// <summary>The current per-type staleness token (<c>type_…</c> form) for a resolved type.</summary>
    string TypeTokenFor(INamedTypeSymbol type);
}

/// <summary>
/// The <b>plugin staging facade</b>: stage semantic ops into the active session. Deliberately
/// <i>without</i> <c>commit</c> and <i>without</i> the raw <see cref="Solution"/>, so version2.md §1's
/// "only commit writes source" invariant falls out of the type system — a plugin physically cannot
/// reach disk or bypass the token model. <see cref="ExecuteAsync"/> is the one staging entry; the
/// CLI and plugins both go through it, so both hit the universal <see cref="OperationGrammar"/>.
/// <para>
/// <b>Contract not frozen.</b> version2.md parks the exact <see cref="IHostStaging"/> surface until a
/// phase-2 plugin stresses it — this is the minimal shape that serves the built-in edit verbs.
/// </para>
/// </summary>
internal interface IHostStaging
{
    /// <summary>
    /// Validate and stage a batch (version2.md, <i>Request model</i>): structural grammar check on
    /// every op, then per-group staleness up front — <b>any</b> stale group rejects the <b>whole</b>
    /// batch (nothing stages, exit 5 carrying the current shapes); all valid → apply all, ops within
    /// a group ordered + sequential. On success, returns the framed read of what changed.
    /// </summary>
    Task<Cs4AiResult> ExecuteAsync(IEnumerable<TypeOperations> batch, CancellationToken ct);
}

/// <summary>
/// The full engine the built-in commands use — the plugin facades (<see cref="IHostQuery"/> +
/// <see cref="IHostStaging"/>) plus raw staged-<see cref="Solution"/> access the facades omit.
/// <c>commit</c> stays host-owned in phase 1 (it owns the flush + baselines), so it is not here.
/// </summary>
internal interface ICSEngine : IHostQuery, IHostStaging
{
    /// <summary>The staged view (the session's fork). Built-in reads use it directly; the plugin
    /// facade omits it.</summary>
    Solution Staged { get; }
}
