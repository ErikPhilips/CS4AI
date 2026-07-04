namespace ErikPhilips.Cs4Ai;

/// <summary>How a field relates to an op.</summary>
internal enum FieldClass
{
    /// <summary>Must be present — absent → structural failure (exit 1).</summary>
    Required,
    /// <summary>May be present or absent.</summary>
    Optional,
    /// <summary>Must be absent — present → structural failure (exit 1). <b>Rejects, never silently
    /// ignores</b> (a silent drop lies to the agent).</summary>
    Forbidden,
}

/// <summary>
/// The <b>operation grammar</b>: a per-op classification table (version2.md, <i>Request model</i>).
/// It is the one place an <see cref="Operation"/>'s field shape is judged, and it lives engine-side
/// and <b>universal</b> — argv-built and plugin-built bags hit the same <see cref="ValidateStructural"/>,
/// so a plugin cannot bypass the grammar. The same table drives <c>--help</c> (one source, no drift).
/// <para>
/// Validation is two-staged: <b>structural</b> here (required missing <i>or</i> forbidden present →
/// exit 1), then <b>semantic</b> downstream (resolve / ambiguous / stale → exit 4 / 3 / 5). A
/// <b>meta-test</b> asserts every (op, field) pair is classified — an unclassified pair is a spec
/// hole that fails the suite, the exhaustiveness a discriminated union would give for free.
/// </para>
/// </summary>
internal static class OperationGrammar
{
    // R/O/F shorthands keep the table scannable as a grid.
    private const FieldClass R = FieldClass.Required;
    private const FieldClass O = FieldClass.Optional;
    private const FieldClass F = FieldClass.Forbidden;

    /// <summary>
    /// The classification table. Read each row as a grid over
    /// <c>Source · Destination · Path · InFile · Namespace · Usings · Body · XmlComment · Attributes · File</c>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<OperationField, FieldClass>> Table =
        new Dictionary<string, IReadOnlyDictionary<OperationField, FieldClass>>(StringComparer.Ordinal)
        {
            //                Src  Dest  Path  InFile  Ns   Using  Body  Xml  Attrs  File
            [Ops.Create] = Row(  F,   R,    O,    O,     F,    F,     R,    F,    O,    F ),
            [Ops.Update] = Row(  R,   F,    F,    O,     O,    O,     O,    O,    O,    O ),  // + at-least-one (below)
            [Ops.Rename] = Row(  R,   R,    F,    O,     F,    F,     F,    F,    F,    O ),
            [Ops.Delete] = Row(  R,   F,    F,    F,     F,    F,     F,    F,    F,    F ),
            [Ops.Move]   = Row(  R,   R,    F,    O,     F,    F,     F,    F,    F,    F ),
        };

    /// <summary>Ops with an extra "at least one of these optionals must be present" structural rule,
    /// and the optional fields it ranges over. <c>update</c> is body-replace <i>or</i> a facet
    /// change (<c>--set-comment</c>/<c>--set-namespace</c>/<c>--set-usings</c>/<c>--set-attributes</c>)
    /// — but never empty.</summary>
    private static readonly IReadOnlyDictionary<string, OperationField[]> AtLeastOneOf =
        new Dictionary<string, OperationField[]>(StringComparer.Ordinal)
        {
            [Ops.Update] = [OperationField.Body, OperationField.Namespace, OperationField.Usings,
                            OperationField.XmlComment, OperationField.Attributes, OperationField.File],
        };

    private static IReadOnlyDictionary<OperationField, FieldClass> Row(
        FieldClass src, FieldClass dest, FieldClass path, FieldClass inFile,
        FieldClass ns, FieldClass usings, FieldClass body, FieldClass xml, FieldClass attrs,
        FieldClass file) =>
        new Dictionary<OperationField, FieldClass>
        {
            [OperationField.Source]      = src,
            [OperationField.Destination] = dest,
            [OperationField.Path]        = path,
            [OperationField.InFile]      = inFile,
            [OperationField.Namespace]   = ns,
            [OperationField.Usings]      = usings,
            [OperationField.Body]        = body,
            [OperationField.XmlComment]  = xml,
            [OperationField.Attributes]  = attrs,
            [OperationField.File]        = file,
        };

    /// <summary>Every classified op name (the table's key set) — for the exhaustiveness meta-test.</summary>
    public static IEnumerable<string> ClassifiedOps => Table.Keys;

    /// <summary>The classification for one (op, field) pair, or null if the op is unknown or the
    /// pair is unclassified (the meta-test asserts this never returns null for a known field).</summary>
    public static FieldClass? ClassOf(string op, OperationField field) =>
        Table.TryGetValue(op, out var row) && row.TryGetValue(field, out var c) ? c : null;

    /// <summary>
    /// Stage-one structural validation: required fields present, forbidden fields absent, plus any
    /// "at least one of" rule. Returns null when the bag's shape is legal (semantic checks follow
    /// downstream), or an exit-1 usage error naming the first offending field.
    /// </summary>
    public static Cs4AiResult? ValidateStructural(Operation op)
    {
        if (!Table.TryGetValue(op.Op, out var row))
            return Cs4AiResult.UsageError($"unknown operation '{op.Op}'.");

        foreach (var field in row.Keys)
        {
            bool present = ValueOf(op, field) is not null;
            switch (row[field])
            {
                case FieldClass.Required when !present:
                    return Cs4AiResult.UsageError($"{op.Op}: missing required field '{FlagName(field)}'.");
                case FieldClass.Forbidden when present:
                    return Cs4AiResult.UsageError(
                        $"{op.Op}: field '{FlagName(field)}' is not allowed for this operation.");
            }
        }

        if (AtLeastOneOf.TryGetValue(op.Op, out var anyOf) &&
            !anyOf.Any(f => ValueOf(op, f) is not null))
            return Cs4AiResult.UsageError(
                $"{op.Op}: supply at least one of {string.Join(", ", anyOf.Select(FlagName))}.");

        return null;
    }

    /// <summary>The required/optional fields of an op, for <c>--help</c> (forbidden fields are
    /// simply not listed). Drawn from the same table the validator uses, so help can't drift.</summary>
    public static (IReadOnlyList<string> required, IReadOnlyList<string> optional) HelpFor(string op)
    {
        if (!Table.TryGetValue(op, out var row))
            return ([], []);
        var required = row.Where(kv => kv.Value == FieldClass.Required).Select(kv => FlagName(kv.Key)).ToList();
        var optional = row.Where(kv => kv.Value == FieldClass.Optional).Select(kv => FlagName(kv.Key)).ToList();
        return (required, optional);
    }

    /// <summary>
    /// The flags an interpreter for <paramref name="op"/> may accept — every non-forbidden field's
    /// flag, plus the content-channel flags (<c>--from</c>/<c>--text</c>/<c>--stdin</c>) when the op
    /// takes a body, plus the universal <c>--dry-run</c>. Anything else is an unknown flag → exit 1.
    /// Drawn from the same table as the validator, so the accepted surface can't drift from the spec.
    /// (<c>--session</c>/<c>--token</c> are universal routing/staleness flags, stripped by the
    /// dispatcher before the interpreter sees argv, so they are intentionally absent here.)
    /// </summary>
    public static IReadOnlySet<string> AllowedFlags(string op)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal) { "--dry-run" };
        if (!Table.TryGetValue(op, out var row)) return flags;

        foreach (var (field, cls) in row)
        {
            if (cls == FieldClass.Forbidden) continue;
            var flag = FlagName(field);
            if (flag.StartsWith("--", StringComparison.Ordinal)) flags.Add(flag); // positionals excluded
        }
        // The body's content can arrive through the content channel; only meaningful if Body is allowed.
        if (row.TryGetValue(OperationField.Body, out var bodyClass) && bodyClass != FieldClass.Forbidden)
        {
            flags.Add("--from");
            flags.Add("--text");
            flags.Add("--stdin");
        }
        return flags;
    }

    private static string? ValueOf(Operation op, OperationField field) => field switch
    {
        OperationField.Source      => op.Source,
        OperationField.Destination => op.Destination,
        OperationField.Path        => op.Path,
        OperationField.InFile      => op.InFile,
        OperationField.Namespace   => op.Namespace,
        OperationField.Usings      => op.Usings,
        OperationField.Body        => op.Body,
        OperationField.XmlComment  => op.XmlComment,
        OperationField.Attributes  => op.Attributes,
        OperationField.File        => op.File,
        _ => null,
    };

    /// <summary>The CLI flag/positional an agent would see for a field — used in error text and help.</summary>
    private static string FlagName(OperationField field) => field switch
    {
        OperationField.Source      => "<address>",
        OperationField.Destination => "<destination>",
        OperationField.Path        => "--path",
        OperationField.InFile      => "--in-file",
        OperationField.Namespace   => "--set-namespace",
        OperationField.Usings      => "--set-usings",
        OperationField.Body        => "--set-body",
        OperationField.XmlComment  => "--set-comment",
        OperationField.Attributes  => "--set-attributes",
        OperationField.File        => "--set-file",
        _ => field.ToString(),
    };
}
