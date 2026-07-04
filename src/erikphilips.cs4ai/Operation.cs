namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The raw, irreducible engine op — a <b>flat optional-bag</b> with a string <see cref="Op"/>
/// discriminator (version2.md, <i>Request model</i>). It deliberately is <b>not</b> a discriminated
/// union: the flat shape relocates to runtime (the <see cref="OperationGrammar"/> validator) what a
/// DU would enforce at compile time, so a <b>single</b> validator governs both argv-built and
/// plugin-built bags — a plugin cannot bypass the grammar.
/// <para>
/// Address convention (uniform across ops): <see cref="Source"/> is an <i>existing</i> address (the
/// full member path including its own name); <see cref="Destination"/> is op-keyed — a new-member
/// address (create), a new name (rename), or a target container (move). The verb sets the existence
/// precondition: <c>create</c>'s leaf must not exist (longest existing prefix is its parent); every
/// other op's <see cref="Source"/> must exist.
/// </para>
/// <para>
/// Built by the <c>SemanticOperation.From*</c> factories from typed commands — the one place
/// argv-shaped (or plugin-shaped) intent becomes a bag — never hand-assembled by callers.
/// </para>
/// </summary>
internal sealed record Operation
{
    /// <summary>The op-name discriminator — one of <see cref="Ops"/>. Vocabulary is core and
    /// closed to plugins (a plugin emits these op names, never new primitives).</summary>
    public required string Op { get; init; }

    /// <summary>An existing address (the edit target) — required by update/rename/delete/move,
    /// forbidden on create.</summary>
    public string? Source { get; init; }

    /// <summary>Op-keyed: the new-member FQN (create), the new bare name (rename), or the target
    /// container address (move).</summary>
    public string? Destination { get; init; }

    /// <summary>The decoupled destination folder for <c>create</c> (folder ≠ namespace — the
    /// namespace comes from the FQN in <see cref="Destination"/>).</summary>
    public string? Path { get; init; }

    /// <summary>Which physical file to target when the type is partial (or to split same-named
    /// arities on create). Not in version2.md's sketch list; added because partial-type targeting
    /// is real (v1's <c>--in</c>) and has nowhere else to live.</summary>
    public string? InFile { get; init; }

    /// <summary>The namespace for <c>update --set-namespace</c> (cascades the FQN; type-token
    /// guarded via the DocId).</summary>
    public string? Namespace { get; init; }

    /// <summary>The file's import set for <c>update --set-usings</c> (file-header-token guarded).</summary>
    public string? Usings { get; init; }

    /// <summary>The full member/type declaration text (create, or update's body replace). The
    /// <i>kind</i> (class/struct/record/…) falls out of parsing it — there is no <c>--kind</c>.</summary>
    public string? Body { get; init; }

    /// <summary>Doc-comment trivia for <c>update --set-comment</c> (no cascade).</summary>
    public string? XmlComment { get; init; }

    /// <summary>Comma-separated attribute lists (<c>[A],[B]</c>) for <c>--set-attributes</c> — a
    /// <b>whole replace</b> of the symbol's attributes, like <c>--set-body</c>. Part of the type
    /// token already (no new grain). Not on delete (removing an attribute is an update to the
    /// reduced set).</summary>
    public string? Attributes { get; init; }

    /// <summary>The file the addressed <b>type</b> should live in, for <c>update</c>/<c>rename</c>
    /// <c>--set-file</c>. Intra-project, relative to the owning project's directory. Renames the whole
    /// file when the type is alone in it (<c>git mv</c> when tracked), or extracts the type into a new
    /// file when the source file holds other types.</summary>
    public string? File { get; init; }
}

/// <summary>
/// One group of <see cref="Operation"/>s against a single type, carrying that type's staleness
/// token <b>once</b> (version2.md, <i>Request model</i>) — not per op. Citing the token per group
/// dissolves intra-batch staleness: an op that ticks the type's token doesn't invalidate the next
/// op in the same group, because the group was planned from one view.
/// </summary>
internal sealed record TypeOperations
{
    /// <summary>The per-type staleness token (<c>type_…</c>). Null on a group whose ops need no
    /// prior view (e.g. creating a brand-new top-level type).</summary>
    public string? Token { get; init; }

    /// <summary>The file-header token (file usings + file attributes) — the one documented second
    /// grain, cited only when a group carries a <c>--set-usings</c> op.</summary>
    public string? FileHeaderToken { get; init; }

    public required IReadOnlyList<Operation> Ops { get; init; }
}

/// <summary>The op-name registry — the closed vocabulary of semantic (staged) ops. Structural
/// (immediate) verbs do not appear here; they never enter <c>Execute</c>'s batch.</summary>
internal static class Ops
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Rename = "rename";
    public const string Delete = "delete";
    public const string Move   = "move";

    /// <summary>Every known op name. The grammar's exhaustiveness meta-test iterates this.</summary>
    public static readonly IReadOnlyList<string> All = [Create, Update, Rename, Delete, Move];
}

/// <summary>The fields of <see cref="Operation"/> that the grammar classifies. One enum value per
/// nullable field on the bag (the discriminator <c>Op</c> is not classified — it selects the row).</summary>
internal enum OperationField
{
    Source,
    Destination,
    Path,
    InFile,
    Namespace,
    Usings,
    Body,
    XmlComment,
    Attributes,
    File,
}
