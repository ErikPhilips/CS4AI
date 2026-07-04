namespace ErikPhilips.Cs4Ai;

/// <summary>
/// <c>cs4ai update &lt;sess-token&gt; &lt;address&gt; --token &lt;type-token&gt;
/// [--set-body &lt;decl&gt; | --from &lt;file&gt;] [--set-comment &lt;xml&gt;] [--set-namespace &lt;ns&gt;]
/// [--set-usings &lt;imports&gt;]</c> — replace a member from its full declaration (the engine diffs and
/// cascades only if the signature changed), or change a facet. The "at least one facet" rule is the
/// grammar's; this interpreter just maps. <c>--set-usings</c> is file-header-token guarded, the
/// others type-token guarded — that grain split lands in Step 4.
/// </summary>
internal sealed class UpdateInterpreter : IEditInterpreter
{
    public string Op => Ops.Update;

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
                "update: usage: cs4ai update <sess-token> <address> --token <type-token> " +
                "[--set-body <decl> | --set-comment <xml> | --set-namespace <ns> | --set-usings <imports>]"));

        // Body is optional for update; resolve it but don't require it (facet-only updates are valid).
        var (body, bErr) = await InterpreterSupport.ResolveBodyAsync(p, stdin);
        if (bErr is { } be) return (null, be);

        var cmd = new UpdateCommand
        {
            Source = p.Positionals[1],
            Body = body,
            XmlComment = p.SetComment,
            Namespace = p.SetNamespace,
            Usings = p.SetUsings,
            Attributes = p.SetAttributes,
            File = p.SetFile,
            InFile = p.InFile,
        };
        // FileHeaderToken plumbing (the usings grain) lands in Step 4; pass the cited type token now.
        return (SemanticOperation.FromUpdate(session!, p.Token, fileHeaderToken: null, cmd), null);
    }
}
