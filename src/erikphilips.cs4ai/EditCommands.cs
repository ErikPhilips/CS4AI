namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Typed, argv-shaped intents for the staged edit verbs — the intermediate the per-command
/// interpreters produce and the <c>SemanticOperation.From*</c> factories consume. These carry only
/// the <i>operation</i> fields; routing (<c>--session</c>) and staleness (<c>--token</c>) tokens
/// are passed alongside to the factory, never embedded here.
/// <para>
/// A plugin with structured intent can construct these directly and call the matching factory —
/// the same path the CLI interpreters take — so this layer is plugin-reachable, not CLI-private.
/// </para>
/// </summary>
internal sealed record CreateCommand
{
    /// <summary>The new symbol's FQN (the namespace lives here, not in a flag).</summary>
    public required string Destination { get; init; }
    /// <summary>Decoupled destination folder (folder ≠ namespace).</summary>
    public string? Path { get; init; }
    /// <summary>The file to place the new type in: co-locates into an existing file whose path suffix
    /// matches (its namespace must equal the type's), otherwise names a new file. Also picks the
    /// target file when adding a member to a partial type.</summary>
    public string? InFile { get; init; }
    /// <summary>The full declaration; its kind falls out of parsing.</summary>
    public required string Body { get; init; }
    /// <summary>Attributes (<c>[A],[B]</c>) for the new symbol — a whole set.</summary>
    public string? Attributes { get; init; }
}

internal sealed record UpdateCommand
{
    /// <summary>The existing member/type to update.</summary>
    public required string Source { get; init; }
    public string? Body { get; init; }
    public string? XmlComment { get; init; }
    public string? Namespace { get; init; }
    public string? Usings { get; init; }
    /// <summary>Whole-replace the member's attributes (<c>[A],[B]</c>).</summary>
    public string? Attributes { get; init; }
    /// <summary>The file the addressed type should live in (<c>--set-file</c>).</summary>
    public string? File { get; init; }
    /// <summary>Which file of a partial target to move (with <c>--set-file</c>).</summary>
    public string? InFile { get; init; }
}

internal sealed record RenameCommand
{
    public required string Source { get; init; }
    /// <summary>The new bare name (not an FQN).</summary>
    public required string NewName { get; init; }
    /// <summary>The file the renamed type should live in (<c>--set-file</c>).</summary>
    public string? File { get; init; }
    /// <summary>Which file of a partial target to move (with <c>--set-file</c>).</summary>
    public string? InFile { get; init; }
}

internal sealed record DeleteCommand
{
    public required string Source { get; init; }
}

internal sealed record MoveCommand
{
    /// <summary>The member to relocate.</summary>
    public required string Source { get; init; }
    /// <summary>The target container type.</summary>
    public required string TargetType { get; init; }
    /// <summary>Which file of a partial target to move into.</summary>
    public string? InFile { get; init; }
}
