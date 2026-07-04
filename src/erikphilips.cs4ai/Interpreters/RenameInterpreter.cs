namespace ErikPhilips.Cs4Ai;

/// <summary>
/// <c>cs4ai rename &lt;sess-token&gt; &lt;address&gt; &lt;new-name&gt; --token &lt;type-token&gt;</c> —
/// rename a member or type to a new <i>bare name</i> (not an FQN). Dedicated (not folded into update)
/// so it renames a type without retyping the class. Cascades to references (computed fresh, no token).
/// </summary>
internal sealed class RenameInterpreter : IEditInterpreter
{
    public string Op => Ops.Rename;

    public Task<(SemanticOperation? op, Cs4AiResult? error)> ParseAsync(
        string[] args, TextReader? stdin, CancellationToken ct)
    {
        if (InterpreterSupport.RejectUnknownFlags(args, Op) is { } uf)
            return Task.FromResult<(SemanticOperation?, Cs4AiResult?)>((null, uf));

        var p = ArgParse.Parse(args);
        var (session, sErr) = InterpreterSupport.TakeSessionToken(p, Op);
        if (sErr is { } se) return Task.FromResult<(SemanticOperation?, Cs4AiResult?)>((null, se));
        if (InterpreterSupport.ValidateTypeToken(p.Token, Op) is { } tErr)
            return Task.FromResult<(SemanticOperation?, Cs4AiResult?)>((null, tErr));

        if (p.Positionals.Count < 3)
            return Task.FromResult<(SemanticOperation?, Cs4AiResult?)>((null, Cs4AiResult.UsageError(
                "rename: usage: cs4ai rename <sess-token> <address> <new-name> --token <type-token>")));

        var cmd = new RenameCommand
        {
            Source = p.Positionals[1], NewName = p.Positionals[2],
            File = p.SetFile, InFile = p.InFile,
        };
        return Task.FromResult<(SemanticOperation?, Cs4AiResult?)>(
            (SemanticOperation.FromRename(session!, p.Token, cmd), null));
    }
}
