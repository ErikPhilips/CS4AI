namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The edit-verb dispatcher: maps a staged-edit verb to its per-command interpreter. The CLI front
/// looks up the verb here and calls <see cref="IEditInterpreter.ParseAsync"/>; a plugin can do the
/// same, or reach past it to the typed commands + <c>SemanticOperation.From*</c> factories. One
/// registry, so the set of edit verbs has a single source.
/// </summary>
internal static class EditInterpreters
{
    private static readonly IReadOnlyDictionary<string, IEditInterpreter> ByVerb =
        new IEditInterpreter[]
        {
            new CreateInterpreter(),
            new UpdateInterpreter(),
            new RenameInterpreter(),
            new DeleteInterpreter(),
            new MoveInterpreter(),
        }.ToDictionary(i => i.Op, StringComparer.Ordinal);

    /// <summary>The interpreter for a verb, or null if the verb is not a staged-edit verb.</summary>
    public static IEditInterpreter? For(string verb) =>
        ByVerb.TryGetValue(verb, out var i) ? i : null;

    /// <summary>Is this verb one of the staged-edit verbs?</summary>
    public static bool IsEditVerb(string verb) => ByVerb.ContainsKey(verb);

    /// <summary>Every staged-edit verb name.</summary>
    public static IEnumerable<string> Verbs => ByVerb.Keys;
}
