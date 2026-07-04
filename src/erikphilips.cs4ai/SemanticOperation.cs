namespace ErikPhilips.Cs4Ai;

/// <summary>
/// A single staged edit, ready for the engine: the routing token (<see cref="SessionToken"/>), the
/// staleness grains (<see cref="TypeToken"/> and, for usings, <see cref="FileHeaderToken"/>), and
/// the validated-shape <see cref="Operation"/> bag. The <c>From*</c> factories are the <b>one</b>
/// place a typed command becomes an <see cref="Operation"/> — argv-built (via the interpreters) and
/// plugin-built intents converge here, then both hit the universal <see cref="OperationGrammar"/>.
/// <para>
/// The host assembles <see cref="SemanticOperation"/>s into the engine's batch grammar
/// (<c>Execute(IEnumerable&lt;TypeOperations&gt;)</c>): same session → one fork, grouped by type so
/// each type's token is cited once.
/// </para>
/// </summary>
internal sealed record SemanticOperation
{
    /// <summary>The session token (<c>sess_…</c>) that routes to the staged fork.</summary>
    public required string SessionToken { get; init; }

    /// <summary>The cited per-type staleness token (<c>type_…</c>), or null when the op needs no
    /// prior view (creating a brand-new top-level type).</summary>
    public string? TypeToken { get; init; }

    /// <summary>The cited file-header token, only for a <c>--set-usings</c> op.</summary>
    public string? FileHeaderToken { get; init; }

    public required Operation Op { get; init; }

    public static SemanticOperation FromCreate(string sessionToken, string? typeToken, CreateCommand c) =>
        new()
        {
            SessionToken = sessionToken,
            TypeToken = typeToken,
            Op = new Operation
            {
                Op = Ops.Create,
                Destination = c.Destination,
                Path = c.Path,
                InFile = c.InFile,
                Body = c.Body,
                Attributes = c.Attributes,
            },
        };

    public static SemanticOperation FromUpdate(
        string sessionToken, string? typeToken, string? fileHeaderToken, UpdateCommand c) =>
        new()
        {
            SessionToken = sessionToken,
            TypeToken = typeToken,
            FileHeaderToken = fileHeaderToken,
            Op = new Operation
            {
                Op = Ops.Update,
                Source = c.Source,
                Body = c.Body,
                XmlComment = c.XmlComment,
                Namespace = c.Namespace,
                Usings = c.Usings,
                Attributes = c.Attributes,
                File = c.File,
                InFile = c.InFile,
            },
        };

    public static SemanticOperation FromRename(string sessionToken, string? typeToken, RenameCommand c) =>
        new()
        {
            SessionToken = sessionToken,
            TypeToken = typeToken,
            Op = new Operation
            {
                Op = Ops.Rename, Source = c.Source, Destination = c.NewName,
                File = c.File, InFile = c.InFile,
            },
        };

    public static SemanticOperation FromDelete(string sessionToken, string? typeToken, DeleteCommand c) =>
        new()
        {
            SessionToken = sessionToken,
            TypeToken = typeToken,
            Op = new Operation { Op = Ops.Delete, Source = c.Source },
        };

    public static SemanticOperation FromMove(string sessionToken, string? typeToken, MoveCommand c) =>
        new()
        {
            SessionToken = sessionToken,
            TypeToken = typeToken,
            Op = new Operation { Op = Ops.Move, Source = c.Source, Destination = c.TargetType, InFile = c.InFile },
        };
}
