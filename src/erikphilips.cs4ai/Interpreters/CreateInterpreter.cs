namespace ErikPhilips.Cs4Ai;

/// <summary>
/// <c>cs4ai create &lt;sess-token&gt; &lt;new-fqn&gt; --set-body &lt;decl&gt; [--path &lt;folder&gt;]
/// [--in-file &lt;file&gt;]</c> — a new member into a type, or a new top-level type into a project.
/// The namespace comes from the FQN; <c>--path</c> is the decoupled folder; there is no
/// <c>--namespace</c>. The kind falls out of parsing the body.
/// </summary>
internal sealed class CreateInterpreter : IEditInterpreter
{
    public string Op => Ops.Create;

    public async Task<(SemanticOperation? op, Cs4AiResult? error)> ParseAsync(
        string[] args, TextReader? stdin, CancellationToken ct)
    {
        if (InterpreterSupport.RejectUnknownFlags(args, Op) is { } uf) return (null, uf);

        var p = ArgParse.Parse(args);
        var (session, sErr) = InterpreterSupport.TakeSessionToken(p, Op);
        if (sErr is { } se) return (null, se);
        if (InterpreterSupport.ValidateTypeToken(p.Token, Op) is { } tErr) return (null, tErr);

        if (p.Positionals.Count < 2)
            return (null, Cs4AiResult.UsageError(
                "create: usage: cs4ai create <sess-token> <new-fqn> --set-body <decl> " +
                "[--path <folder>] [--in-file <file>]"));

        var (body, bErr) = await InterpreterSupport.ResolveBodyAsync(p, stdin);
        if (bErr is { } be) return (null, be);
        if (body is null)
            return (null, Cs4AiResult.UsageError(
                "create: missing --set-body (or --from <file>/--text/stdin) — the new declaration."));

        var cmd = new CreateCommand
        {
            Destination = p.Positionals[1],
            Path = p.Path,
            InFile = p.InFile,
            Body = body,
            Attributes = p.SetAttributes,
        };
        return (SemanticOperation.FromCreate(session!, p.Token, cmd), null);
    }
}
