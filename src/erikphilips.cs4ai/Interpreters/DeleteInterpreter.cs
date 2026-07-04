namespace ErikPhilips.Cs4Ai;

/// <summary>
/// <c>cs4ai delete &lt;sess-token&gt; &lt;address&gt; --token &lt;type-token&gt;</c> — remove a member
/// or a whole type. A body or any other facet flag is forbidden by the grammar (<c>delete --set-body</c>
/// → exit 1), so this interpreter accepts none.
/// </summary>
internal sealed class DeleteInterpreter : IEditInterpreter
{
    public string Op => Ops.Delete;

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

        if (p.Positionals.Count < 2)
            return Task.FromResult<(SemanticOperation?, Cs4AiResult?)>((null, Cs4AiResult.UsageError(
                "delete: usage: cs4ai delete <sess-token> <address> --token <type-token>")));

        var cmd = new DeleteCommand { Source = p.Positionals[1] };
        return Task.FromResult<(SemanticOperation?, Cs4AiResult?)>(
            (SemanticOperation.FromDelete(session!, p.Token, cmd), null));
    }
}
