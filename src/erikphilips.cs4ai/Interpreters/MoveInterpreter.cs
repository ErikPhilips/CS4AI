namespace ErikPhilips.Cs4Ai;

/// <summary>
/// <c>cs4ai move &lt;sess-token&gt; &lt;member-address&gt; &lt;target-type&gt; --token &lt;type-token&gt;
/// [--in-file &lt;file&gt;]</c> — relocate a member to another type, rewriting references (computed fresh).
/// Source then target are both positionals (uniform with the grammar's positional Destination); no
/// <c>--to</c> flag.
/// </summary>
internal sealed class MoveInterpreter : IEditInterpreter
{
    public string Op => Ops.Move;

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
                "move: usage: cs4ai move <sess-token> <member-address> <target-type> " +
                "--token <type-token> [--in-file <file>]")));

        var cmd = new MoveCommand
        {
            Source = p.Positionals[1],
            TargetType = p.Positionals[2],
            InFile = p.InFile,
        };
        return Task.FromResult<(SemanticOperation?, Cs4AiResult?)>(
            (SemanticOperation.FromMove(session!, p.Token, cmd), null));
    }
}
