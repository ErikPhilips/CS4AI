using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The Roslyn engine behind every staged edit (version2.md, <i>The seam</i>). Bound to one
/// <see cref="EditSession"/> + the repo's <see cref="Cs4AiConfig"/>; <see cref="Staged"/> is the
/// session's fork. <see cref="ExecuteAsync"/> is the single staging entry — the CLI (via the
/// interpreters) and plugins both reach it, so both pass through the universal
/// <see cref="OperationGrammar"/>. Orchestrates the existing cores (<see cref="AddressResolver"/>,
/// <see cref="Canonicalizer"/>, <see cref="TokenBuilder"/>, <see cref="SymbolRenderer"/>, the
/// <see cref="Renamer"/>); it does not own <c>commit</c> (the host flushes).
/// </summary>
internal sealed class CSEngine : ICSEngine
{
    public const string TypeTokenPrefix = "type_";

    private readonly SolutionHost _host;
    private readonly EditSession _session;
    private readonly Cs4AiConfig _config;
    private readonly Func<string?, string> _relativize;

    public CSEngine(SolutionHost host, EditSession session, Cs4AiConfig config)
    {
        _host = host;
        _session = session;
        _config = config;
        _relativize = host.Relativize;
    }

    /// <summary>The single live view (== disk). The pivot removed the staged fork; this is the host's
    /// warm <see cref="Solution"/>, mutated in place by each write-through edit.</summary>
    public Solution Staged => _host.CurrentSolution
        ?? throw new InvalidOperationException("live solution not loaded");

    public string TypeTokenFor(INamedTypeSymbol type) => TypeTokenPrefix + TokenBuilder.TokenString(type);

    public Task<(AddressResolver.Resolved result, Cs4AiResult? error)> ResolveAsync(
        string address, CancellationToken ct) => AddressResolver.ResolveAsync(Staged, address, ct);

    public Task<(IReadOnlyList<ISymbol> matches, Cs4AiResult? error)> ResolveManyAsync(
        string address, CancellationToken ct) => AddressResolver.ResolveManyAsync(Staged, address, ct);

    // ─────────────────────────────────────────────────────────────────────────────
    //  Execute — validate (all-or-nothing) → apply → canonicalize → framed restate.
    // ─────────────────────────────────────────────────────────────────────────────

    public async Task<Cs4AiResult> ExecuteAsync(IEnumerable<TypeOperations> batch, CancellationToken ct)
    {
        var groups = batch.ToList();
        if (groups.Count == 0 || groups.All(g => g.Ops.Count == 0))
            return Cs4AiResult.UsageError("execute: no operations.");

        // Stage 1: structural grammar check on every op — any failure rejects the batch (exit 1).
        foreach (var g in groups)
            foreach (var op in g.Ops)
                if (OperationGrammar.ValidateStructural(op) is { } structErr) return structErr;

        // Stage 2: plan + staleness up front, against the *unmutated* live view. Resolve errors
        // (exit 3/4) fail the whole batch; stale groups (exit 5) collect into one inspect-shaped
        // rejection — nothing is written either way.
        var original = await _host.GetCommittedSolutionAsync(ct);
        var sol = original;
        var plans = new List<GroupPlan>();
        var staleTypes = new List<INamedTypeSymbol>();
        foreach (var g in groups)
        {
            var (plan, err) = await PlanGroupAsync(sol, g, ct);
            if (err is { } e) return e;
            if (plan!.StaleType is { } st) { staleTypes.Add(st); continue; }
            plans.Add(plan);
        }
        if (staleTypes.Count > 0) return StaleResult(staleTypes);

        // Stage 3: apply. Each op yields one or more restate descriptors (move yields two).
        var restates = new List<Restate>();
        foreach (var plan in plans)
            foreach (var op in plan.Group.Ops)
            {
                var (next, applyErr, ops) = await ApplyAsync(sol, op, plan, ct);
                if (applyErr is { } ae) return ae;
                sol = next;
                restates.AddRange(ops);
            }

        // Stage 4: canonicalize each framed (touched) type so the staged view stays canonical.
        foreach (var docId in restates.Where(r => r.FrameDocId is not null)
                                      .Select(r => r.FrameDocId!).Distinct().ToList())
        {
            var type = await FindTypeByDocIdAsync(sol, docId, ct);
            if (type is not null) (sol, _) = await CanonicalizeAsync(sol, type, ct);
        }

        // Stage 4.5 (Phase 1.5): silently remove using directives this op left broken (CS0246) —
        // quiet the noise cs4ai created before the result is rendered. The baseline scopes it to
        // cs4ai-caused breakage only: a CS0246 that predates the session is never touched.
        sol = await SelfHeal.QuietBrokenUsingsAsync(sol, original, _session.RoslynBaseline, _relativize, ct);

        // Stage 5: WRITE THROUGH. Every op landed in memory → flush the touched files to disk and
        // adopt `sol` as the host's live view. Atomic per command (nothing was written until here);
        // git owns cross-command undo. No staging, no commit.
        await _host.AdoptSolutionAsync(sol, ct);
        _session.LastTests = "none"; // tests are stale after any edit (not recomputed here — expensive)

        // Stage 5.5: refresh the build axis from Roslyn (CS* only — a code edit can't touch the
        // project/restore layer), tagged against the CS-only RoslynBaseline.
        if (_session.RoslynBaseline is { } baseline)
            _session.CachedOutcome = await BuildOutcomes.ComputeAsync(sol, baseline, _relativize, ct);

        // Stage 6: render the framed line-stream — a status line, then a block per restate. A plugin
        // macro returns the concatenation of these; no parsing needed.
        var lines = new List<string> { "ok" };
        foreach (var r in restates)
        {
            if (r.FrameDocId is null)
            {
                lines.Add(r.Delta ?? "(changed)");
            }
            else
            {
                var type = await FindTypeByDocIdAsync(sol, r.FrameDocId, ct);
                if (type is null) lines.Add(r.Delta ?? "(changed)");
                else if (r.FullBody) lines.AddRange(FrameRenderer.FullType(type, _relativize, r.Op));
                else lines.AddRange(FrameRenderer.DeltaType(type, r.Delta ?? "", _relativize, r.Op));
            }
            if (r.Note is not null) lines.Add(r.Note);
        }

        // The build axis rides as a trailing block — the agent sees, on every edit, whether the
        // staged code compiles and what it newly broke, without a separate `build` call.
        if (_session.CachedOutcome is { } outcome)
            lines.AddRange(FrameRenderer.BuildBlock(outcome));

        return Cs4AiResult.Edited(string.Join("\n", lines) + "\n");
    }

    /// <summary>A restate descriptor — one framed block in the result. <see cref="Op"/> leads the
    /// frame header; <see cref="FrameDocId"/> null means a bare line (a whole-type delete);
    /// <see cref="FullBody"/> chooses full source vs the one-line <see cref="Delta"/>;
    /// <see cref="Note"/> is an extra line after the frame (inferred usings).</summary>
    private sealed record Restate(string Op, string? FrameDocId, bool FullBody, string? Delta, string? Note = null);

    // ─────────────────────────────────────────────────────────────────────────────
    //  Planning + staleness
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>What an op needs at apply time, resolved once against the unmutated view.</summary>
    private sealed record GroupPlan
    {
        public required TypeOperations Group { get; init; }
        /// <summary>The existing anchor type (member-create parent / update/rename/delete/move
        /// target), or null for a brand-new top-level type create.</summary>
        public INamedTypeSymbol? AnchorType { get; init; }
        /// <summary>The namespace for a new top-level type create (null otherwise).</summary>
        public string? NewTypeNamespace { get; init; }
        public string? NewTypeLeaf { get; init; }
        /// <summary>Set when this group is stale — the current type to frame in the rejection.</summary>
        public INamedTypeSymbol? StaleType { get; init; }
    }

    private async Task<(GroupPlan? plan, Cs4AiResult? error)> PlanGroupAsync(
        Solution sol, TypeOperations group, CancellationToken ct)
    {
        if (group.Ops.Count == 0)
            return (null, Cs4AiResult.UsageError("execute: empty operation group."));

        // One group = one type. Plan from the first op; phase-1 CLI groups carry exactly one.
        var first = group.Ops[0];

        if (first.Op == Ops.Create)
        {
            var (parentType, ns, leaf, perr) = await ResolveCreateTargetAsync(sol, first.Destination!, ct);
            if (perr is { } pe) return (null, pe);

            if (parentType is not null)
            {
                // member create into an existing type → cites that type's token.
                var stale = IsStale(parentType, group.Token) ? parentType : null;
                return (new GroupPlan { Group = group, AnchorType = parentType, StaleType = stale }, null);
            }
            // new top-level type → no prior view, no token expected.
            return (new GroupPlan { Group = group, NewTypeNamespace = ns, NewTypeLeaf = leaf }, null);
        }

        // update / rename / delete / move: Source must resolve; anchor is its (containing) type.
        var (resolved, rerr) = await AddressResolver.ResolveAsync(sol, first.Source!, ct);
        if (rerr is { } re) return (null, re);
        if (resolved.Symbol is null)
            return (null, Cs4AiResult.NotFound($"address not found: '{first.Source}'"));

        var anchor = resolved.Symbol as INamedTypeSymbol ?? resolved.Symbol.ContainingType;
        if (anchor is null)
            return (null, Cs4AiResult.UsageError($"cannot determine containing type for '{first.Source}'"));

        var staleType = IsStale(anchor, group.Token) ? anchor : null;
        return (new GroupPlan { Group = group, AnchorType = anchor, StaleType = staleType }, null);
    }

    /// <summary>True when the cited token doesn't match the type's current token (stale or missing) —
    /// the same condition with the same recovery (the rejection is the read).</summary>
    private bool IsStale(INamedTypeSymbol type, string? citedToken) =>
        citedToken is null ||
        !string.Equals(citedToken, TypeTokenFor(type), StringComparison.OrdinalIgnoreCase);

    /// <summary>Exit 5: nothing staged. The rejection IS the read — a fail line plus the current
    /// frame (full shape + fresh token) for each stale type, so the agent re-cites and re-fires.</summary>
    private Cs4AiResult StaleResult(List<INamedTypeSymbol> staleTypes)
    {
        var lines = new List<string>();
        foreach (var type in staleTypes)
        {
            lines.Add($"stale: {AddressResolver.Render(type)} — re-cite the current token below");
            lines.AddRange(FrameRenderer.FullType(type, _relativize, "stale"));
        }
        return Cs4AiResult.Stale(string.Join("\n", lines) + "\n");
    }

    /// <summary>
    /// Decide whether a create address names a new member (parent resolves to a type) or a new
    /// top-level type (parent is a namespace). Returns the parent type for the former, or the
    /// (namespace, leaf) for the latter.
    /// </summary>
    private static async Task<(INamedTypeSymbol? parent, string? ns, string? leaf, Cs4AiResult? error)>
        ResolveCreateTargetAsync(Solution sol, string destination, CancellationToken ct)
    {
        var parenIdx = destination.IndexOf('(');
        var namePart = parenIdx > 0 ? destination[..parenIdx] : destination;
        var segs = namePart.Split('.');
        if (segs.Length < 2)
            return (null, null, null, Cs4AiResult.UsageError(
                $"create: '{destination}' needs a namespace-qualified target (Namespace.Type or Type.Member)."));

        var leaf = segs[^1];
        var parentFqn = string.Join('.', segs[..^1]);

        var (resolved, err) = await AddressResolver.ResolveAsync(sol, parentFqn, ct);
        if (err is { } e)
        {
            // Not-found parent → it's a namespace → new top-level type. Ambiguous → propagate.
            if (e.ExitCode == Cs4AiResult.CodeNotFound)
                return (null, parentFqn, leaf, null);
            return (null, null, null, e);
        }
        if (resolved.Symbol is INamedTypeSymbol parentType)
            return (parentType, null, null, null);

        return (null, null, null, Cs4AiResult.UsageError(
            $"create: parent '{parentFqn}' is not a type — cannot create '{leaf}' inside it."));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Apply (per op)
    // ─────────────────────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<Restate> NoRestate = [];

    private async Task<(Solution sol, Cs4AiResult? error, IReadOnlyList<Restate> restates)> ApplyAsync(
        Solution sol, Operation op, GroupPlan plan, CancellationToken ct) => op.Op switch
    {
        Ops.Create => await ApplyCreateAsync(sol, op, plan, ct),
        Ops.Update => await ApplyUpdateAsync(sol, op, ct),
        Ops.Rename => await ApplyRenameAsync(sol, op, ct),
        Ops.Delete => await ApplyDeleteAsync(sol, op, ct),
        Ops.Move   => await ApplyMoveAsync(sol, op, ct),
        _ => (sol, Cs4AiResult.UsageError($"unknown operation '{op.Op}'."), NoRestate),
    };

    private async Task<(Solution, Cs4AiResult?, IReadOnlyList<Restate>)> ApplyCreateAsync(
        Solution sol, Operation op, GroupPlan plan, CancellationToken ct)
    {
        var member = SyntaxFactory.ParseMemberDeclaration(op.Body!);
        if (member is null || member.ContainsDiagnostics)
            return (sol, Cs4AiResult.UsageError("create: --set-body is not a valid declaration."), NoRestate);

        if (op.Attributes is not null)
        {
            var (attrLists, attrErr) = ParseAttributeLists(op.Attributes);
            if (attrErr is { } ae) return (sol, ae, NoRestate);
            member = member.WithAttributeLists(attrLists); // whole replace
        }

        // ── new member into an existing type ── (restate the type in full)
        if (plan.AnchorType is { } anchor)
        {
            SyntaxReference? targetDecl = PickDeclaration(anchor, op.InFile);
            if (targetDecl is null)
                return (sol, Cs4AiResult.UsageError(
                    $"create: '{anchor.Name}' is partial; --in-file required to pick the file."), NoRestate);

            var doc = sol.GetDocument(targetDecl.SyntaxTree);
            if (doc is null) return (sol, Cs4AiResult.FileError("create: target document not found."), NoRestate);
            var root = await targetDecl.SyntaxTree.GetRootAsync(ct);
            var typeNode = (TypeDeclarationSyntax)await targetDecl.GetSyntaxAsync(ct);
            var newRoot = root.ReplaceNode(typeNode, typeNode.AddMembers(member));
            return (doc.WithSyntaxRoot(newRoot).Project.Solution, null,
                [new Restate(Ops.Create, anchor.GetDocumentationCommentId(), FullBody: true, null)]);
        }

        // ── new top-level type into a project ──
        if (member is not BaseTypeDeclarationSyntax and not DelegateDeclarationSyntax)
            return (sol, Cs4AiResult.UsageError(
                "create: a namespace-level address requires a type/delegate declaration body."), NoRestate);

        var ns = plan.NewTypeNamespace!;
        var project = PickProjectByNamespace(sol, ns);
        if (project is null)
            return (sol, Cs4AiResult.UsageError(
                $"create: no project matches namespace '{ns}'. Name a --path under a project, or " +
                "add the project first."), NoRestate);

        var leaf = StripGenerics(plan.NewTypeLeaf!);
        var projDir = Path.GetDirectoryName(project.FilePath) ?? Directory.GetCurrentDirectory();

        // --in-file names the file this new type should live in (its documented contract). Match it as
        // a path suffix against the project's files: exactly one → co-locate into it; none → use it as
        // the new file's name; many → ambiguous. Never silently ignore it.
        DocumentId touchedDocId;
        Document? coLocateTarget = null;
        if (op.InFile is not null)
        {
            var norm = op.InFile.Replace('\\', '/');
            var matches = project.Documents
                .Where(d => d.FilePath is { } fp && fp.Replace('\\', '/').EndsWith(norm, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 1)
                return (sol, Cs4AiResult.Ambiguous(
                    $"create --in-file: '{op.InFile}' matches {matches.Count} files — be more specific."), NoRestate);
            coLocateTarget = matches.Count == 1 ? matches[0] : null;
        }

        string? pathNote = null;
        if (coLocateTarget is not null)
        {
            var (coSol, coErr) = await CoLocateTypeAsync(sol, coLocateTarget.Id, member, ns, ct);
            if (coErr is { } ce) return (sol, ce, NoRestate);
            sol = coSol;
            touchedDocId = coLocateTarget.Id;
        }
        else
        {
            // New file: honor an unmatched --in-file as the filename, else <leaf>.cs; --path is the folder.
            var fileName = op.InFile is { } inf
                ? Path.GetFileName(inf.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? inf : inf + ".cs")
                : leaf + ".cs";

            // --path is PROJECT-relative (the project came from the FQN); the natural shell guess is
            // solution-root-relative, which joined verbatim doubles the project directory
            // (src/Proj/src/Proj/…) silently. A --path starting with the project's own directory is
            // never intentional: strip it and echo the correction. Only the full solution-relative
            // prefix normalizes — a bare folder that merely shares the project's name stays literal.
            var rawPath = (op.Path ?? "").Replace('\\', '/').Trim('/');
            if (rawPath.Length > 0)
            {
                if (Path.IsPathRooted(rawPath) || rawPath.Split('/').Contains(".."))
                    return (sol, Cs4AiResult.UsageError(
                        $"create --path: '{op.Path}' escapes the project — --path is project-relative " +
                        "(the project comes from the FQN)."), NoRestate);
                var relProj = _relativize(projDir).Trim('/');
                if (relProj.Length > 0 && relProj != "." && !relProj.StartsWith("..", StringComparison.Ordinal) &&
                    (rawPath.Equals(relProj, StringComparison.OrdinalIgnoreCase) ||
                     rawPath.StartsWith(relProj + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    var stripped = rawPath.Length == relProj.Length ? "" : rawPath[(relProj.Length + 1)..];
                    pathNote = $"--path is project-relative — '{op.Path}' interpreted as " +
                               $"'{(stripped.Length == 0 ? "." : stripped)}'";
                    rawPath = stripped;
                }
            }

            var folders = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var filePath = Path.Combine(new[] { projDir }.Concat(folders).Append(fileName).ToArray());

            // Default-name collision (Result then Result<T> both defaulting to Result.cs): a second
            // AddDocument at the same path forks the in-memory view from disk — write-through clobbers
            // the first type while the workspace still compiles both, so `build` goes falsely green.
            // Co-locate into the existing file instead, exactly as a matched --in-file does.
            var fullPath = Path.GetFullPath(filePath);
            var collision = project.Documents.FirstOrDefault(d =>
                d.FilePath is { } fp && string.Equals(Path.GetFullPath(fp), fullPath, StringComparison.OrdinalIgnoreCase));
            if (collision is not null)
            {
                var (coSol, coErr) = await CoLocateTypeAsync(sol, collision.Id, member, ns, ct);
                if (coErr is { } ce) return (sol, ce, NoRestate);
                sol = coSol;
                touchedDocId = collision.Id;
            }
            else if (File.Exists(fullPath))
            {
                return (sol, Cs4AiResult.FileError(
                    $"create: '{_relativize(fullPath)}' exists on disk but is not part of the project — " +
                    "refusing to overwrite it. Name a different file with --in-file."), NoRestate);
            }
            else
            {
                // Use the (possibly attribute-augmented) member text, not the raw body.
                var fileText = $"namespace {ns};\n\n{member.ToFullString().TrimEnd()}\n";
                var newDoc = project.AddDocument(fileName, fileText, folders.Length > 0 ? folders : null, filePath);
                sol = newDoc.Project.Solution;
                touchedDocId = newDoc.Id;
            }
        }

        // Best-effort: infer unambiguous usings from compile diagnostics on the touched file. Echoed as
        // a note line so the agent sees the guess (and can correct it via update --set-usings).
        var (afterUsings, added) = await InferUsingsAsync(sol, touchedDocId, ct);
        sol = afterUsings;

        // Arity-exact lookup: with Result and Result<T> now legal side by side, a bare-name probe
        // would frame the non-generic sibling instead of the type just created.
        var arity = ArityOf(plan.NewTypeLeaf!);
        var created = await FindTypeByDocIdAsync(sol, $"T:{ns}.{leaf}{(arity > 0 ? $"`{arity}" : "")}", ct);
        var note = JoinNotes(pathNote, added.Count > 0 ? "inferred-usings: " + string.Join(", ", added) : null);
        return (sol, null, [new Restate(Ops.Create, created?.GetDocumentationCommentId(), FullBody: true, null, note)]);
    }

    private async Task<(Solution, Cs4AiResult?, IReadOnlyList<Restate>)> ApplyUpdateAsync(
        Solution sol, Operation op, CancellationToken ct)
    {
        if (op.Namespace is not null)
            return await ApplySetNamespaceAsync(sol, op, ct);

        var (resolved, err) = await AddressResolver.ResolveAsync(sol, op.Source!, ct);
        if (err is { } e) return (sol, e, NoRestate);
        var sym = resolved.Symbol;
        if (sym is null) return (sol, Cs4AiResult.NotFound($"address not found: '{op.Source}'"), NoRestate);

        var frameType = sym as INamedTypeSymbol ?? sym.ContainingType;
        if (frameType is null)
            return (sol, Cs4AiResult.UsageError($"update: cannot determine the type for '{op.Source}'."), NoRestate);

        // ── Facets: --set-body re-declares the addressed thing in place — a member, or a WHOLE type
        //    (same file, same spot; the cited token makes it deliberate). --set-comment and
        //    --set-attributes apply to a type OR a member. ──
        if (op.Body is not null || op.XmlComment is not null || op.Attributes is not null)
        {
            MemberDeclarationSyntax? parsedBody = null;
            if (op.Body is not null)
            {
                parsedBody = SyntaxFactory.ParseMemberDeclaration(op.Body);
                if (parsedBody is null || parsedBody.ContainsDiagnostics)
                    return (sol, Cs4AiResult.UsageError("update: --set-body is not a valid declaration."), NoRestate);
                if (sym is INamedTypeSymbol addressed)
                {
                    // Redeclaring a type: the body must be a type-shaped declaration whose name and
                    // arity match the address — renames go through `rename` (call sites rewritten).
                    var (bodyName, bodyArity) = TypeNameArity(parsedBody);
                    if (bodyArity < 0)
                        return (sol, Cs4AiResult.UsageError(
                            $"update: '{AddressResolver.Render(sym)}' is a type — --set-body must be a " +
                            "whole type declaration (class/struct/record/interface/enum/delegate)."), NoRestate);
                    if (bodyName != addressed.Name || bodyArity != addressed.Arity)
                        return (sol, Cs4AiResult.UsageError(
                            $"update --set-body: declares '{RenderTypeDecl(parsedBody)}' but the address is " +
                            $"'{AddressResolver.Render(sym)}' — name and arity must match; use `rename` to change the name."), NoRestate);
                }
            }

            var declRef = sym is INamedTypeSymbol tsym
                ? PickDeclaration(tsym, op.InFile)                       // honor --in-file for partials
                : sym.DeclaringSyntaxReferences.FirstOrDefault();
            if (declRef is null)
                return (sol, sym is INamedTypeSymbol
                    ? Cs4AiResult.UsageError($"update: '{frameType.Name}' is partial; --in-file required to pick the file.")
                    : Cs4AiResult.FileError("update: member has no declaration syntax."), NoRestate);
            var oldNode = await declRef.GetSyntaxAsync(ct);
            var doc = sol.GetDocument(oldNode.SyntaxTree);
            if (doc is null) return (sol, Cs4AiResult.FileError("update: document not found."), NoRestate);
            var root = await oldNode.SyntaxTree.GetRootAsync(ct);

            SyntaxNode replacement = parsedBody is not null
                ? parsedBody.WithTriviaFrom(oldNode)
                : oldNode; // facet-only — start from the existing node

            if (op.Attributes is not null)
            {
                var (attrLists, attrErr) = ParseAttributeLists(op.Attributes);
                if (attrErr is { } ae) return (sol, ae, NoRestate);
                replacement = ((MemberDeclarationSyntax)replacement).WithAttributeLists(attrLists); // whole replace
            }

            if (op.XmlComment is not null)
                replacement = WithDocComment(replacement, op.XmlComment); // doc comment sits above attributes

            sol = doc.WithSyntaxRoot(root.ReplaceNode(oldNode, replacement)).Project.Solution;
        }

        // ── --set-usings — file-level whole replace (addresses a type or member; no token grain:
        // a declarative new set, like create's new content). File paths are stable across the
        // member edit above, so we re-find the doc in the updated solution by path. ──
        string? note = null;
        if (op.Usings is not null)
        {
            var filePath = PickFilePath(sym, op.InFile);
            if (filePath is null)
                return (sol, Cs4AiResult.UsageError(
                    $"update --set-usings: '{frameType.Name}' spans multiple files; --in-file required to pick one."), NoRestate);

            var (usings, uerr) = ParseUsings(op.Usings);
            if (uerr is { } ue) return (sol, ue, NoRestate);

            var doc = sol.Projects.SelectMany(p => p.Documents)
                         .FirstOrDefault(d => string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (doc is null) return (sol, Cs4AiResult.FileError("update --set-usings: file not found in the solution."), NoRestate);
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root is not CompilationUnitSyntax cu)
                return (sol, Cs4AiResult.FileError("update --set-usings: file root is not a compilation unit."), NoRestate);

            var typeCount = cu.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Count();
            sol = doc.WithSyntaxRoot(cu.WithUsings(usings)).Project.Solution;
            note = $"usings replaced in {_relativize(filePath)} — affects {typeCount} type(s) in the file";
        }

        // ── --set-file — reconcile the type with the file it lives in (types only). Runs last so it
        //    relocates the already-edited content; supersedes the frame with the move delta. ──
        if (op.File is not null)
        {
            if (sym is not INamedTypeSymbol)
                return (sol, Cs4AiResult.UsageError(
                    "update --set-file: addresses a type; relocate members with `move`."), NoRestate);
            // Re-resolve in the (possibly mutated) solution so we move the current declaration.
            var fresh = await FindTypeByDocIdAsync(sol, frameType.GetDocumentationCommentId()!, ct) ?? frameType;
            var (msol, mErr, mRestate) = await ApplySetFileAsync(sol, fresh, op.File, op.InFile, Ops.Update, ct);
            if (mErr is { } me) return (sol, me, NoRestate);
            return (msol, null, mRestate is null ? NoRestate : [mRestate]);
        }

        return (sol, null, [new Restate(Ops.Update, frameType.GetDocumentationCommentId(), FullBody: true, null, note)]);
    }

    /// <summary>
    /// <c>update --set-namespace</c> — relocate a type to a new namespace (version2.md: type-token
    /// guarded, since the namespace is in the DocId). cs4ai doesn't police file=namespace: it changes
    /// the namespace declaration the type sits under (file-scoped namespaces move every type in the
    /// file — noted, not refused). The cascade is handled by adding <c>using &lt;new&gt;;</c> to every
    /// referencing file <i>first</i> (harmless), plus <c>using &lt;old&gt;;</c> to the moved file so it
    /// still sees its former namespace-mates; fully-qualified references fall through to build.
    /// </summary>
    private async Task<(Solution, Cs4AiResult?, IReadOnlyList<Restate>)> ApplySetNamespaceAsync(
        Solution sol, Operation op, CancellationToken ct)
    {
        if (op.Body is not null || op.XmlComment is not null || op.Attributes is not null || op.Usings is not null)
            return (sol, Cs4AiResult.UsageError(
                "update --set-namespace: run it as its own update (don't combine with other facets)."), NoRestate);

        var (resolved, err) = await AddressResolver.ResolveAsync(sol, op.Source!, ct);
        if (err is { } e) return (sol, e, NoRestate);
        var sym = resolved.Symbol;
        if (sym is null) return (sol, Cs4AiResult.NotFound($"address not found: '{op.Source}'"), NoRestate);
        var type = sym as INamedTypeSymbol ?? sym.ContainingType;
        if (type is null) return (sol, Cs4AiResult.UsageError($"--set-namespace: '{op.Source}' has no type."), NoRestate);

        var newNs = op.Namespace!.Trim();
        var oldFqn = AddressResolver.Render(type);
        var oldNs = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();
        var ownFiles = type.DeclaringSyntaxReferences
            .Select(r => r.SyntaxTree.FilePath).Where(p => p is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Fix references in each referencing file: rewrite fully-qualified `OldNs.Type` to
        //    `NewNs.Type`, and add `using <newNs>;` only where the type is referenced by simple name.
        var refs = await SymbolFinder.FindReferencesAsync(type, sol, ct);
        var byDoc = refs.SelectMany(r => r.Locations)
            .Where(l => l.Document.FilePath is not null && !ownFiles.Contains(l.Document.FilePath))
            .GroupBy(l => l.Document.Id)
            .ToList();

        int fqRewrites = 0, usingsAdded = 0;
        foreach (var grp in byDoc)
        {
            var doc = sol.GetDocument(grp.Key);
            if (doc?.FilePath is null || await doc.GetSyntaxRootAsync(ct) is not { } root) continue;

            var fqNodes = new List<SyntaxNode>();
            var hasSimple = false;
            foreach (var loc in grp)
            {
                var fq = FullyQualifiedParent(root.FindNode(loc.Location.SourceSpan));
                if (fq is not null) fqNodes.Add(fq); else hasSimple = true;
            }

            if (fqNodes.Count > 0)
            {
                sol = doc.WithSyntaxRoot(root.ReplaceNodes(fqNodes, (orig, _) => RewriteQualifier(orig, newNs)))
                         .Project.Solution;
                fqRewrites += fqNodes.Count;
            }
            if (hasSimple)
            {
                sol = await AddUsingAsync(sol, doc.FilePath, newNs, ct);
                usingsAdded++;
            }
        }

        // 2. Add `using <oldNs>;` to the moved file(s) so the type still sees former namespace-mates.
        if (oldNs is not null)
            foreach (var path in ownFiles)
                sol = await AddUsingAsync(sol, path!, oldNs, ct);

        // 3. Change the namespace declaration the type sits under (re-find by stable DocId).
        var docId = type.GetDocumentationCommentId();
        var typeNow = (docId is null ? null : await FindTypeByDocIdAsync(sol, docId, ct)) ?? type;
        foreach (var declRef in typeNow.DeclaringSyntaxReferences)
        {
            var doc = sol.GetDocument(declRef.SyntaxTree);
            if (doc is null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root is null) continue;
            var node = root.FindNode(declRef.Span);
            var nsNode = node.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            if (nsNode is null)
                return (sol, Cs4AiResult.UsageError(
                    $"--set-namespace: '{type.Name}' is in the global namespace — not supported yet " +
                    "(add a namespace declaration manually)."), NoRestate);
            BaseNamespaceDeclarationSyntax updated = nsNode switch
            {
                FileScopedNamespaceDeclarationSyntax f => f.WithName(SyntaxFactory.ParseName(newNs)),
                NamespaceDeclarationSyntax b => b.WithName(SyntaxFactory.ParseName(newNs)),
                _ => nsNode,
            };
            sol = doc.WithSyntaxRoot(root.ReplaceNode(nsNode, updated)).Project.Solution;
        }

        var newDocId = $"T:{newNs}.{type.MetadataName}";
        var noteParts = new List<string>();
        if (usingsAdded > 0) noteParts.Add($"added using {newNs}; to {usingsAdded} file(s)");
        if (fqRewrites > 0) noteParts.Add($"rewrote {fqRewrites} fully-qualified reference(s)");
        var note = noteParts.Count > 0 ? string.Join("; ", noteParts) : null;
        return (sol, null,
            [new Restate(Ops.Update, newDocId, FullBody: false, $"{oldFqn} -> {newNs}.{type.Name}", note)]);
    }

    /// <summary>If <paramref name="idNode"/> (a type reference) is the trailing name of a
    /// fully-qualified reference — <c>Old.Ns.Type</c> in a type context (QualifiedName) or an
    /// expression context (MemberAccess) — return that whole qualified node; else null (a simple-name
    /// reference, handled by adding a using).</summary>
    private static SyntaxNode? FullyQualifiedParent(SyntaxNode idNode) => idNode.Parent switch
    {
        QualifiedNameSyntax qn when qn.Right == idNode => qn,
        MemberAccessExpressionSyntax ma when ma.Name == idNode => ma,
        _ => null,
    };

    /// <summary>Swap the namespace qualifier of a fully-qualified reference for <paramref name="newNs"/>,
    /// preserving the type name (incl. generic args): <c>Old.Type&lt;T&gt;</c> → <c>NewNs.Type&lt;T&gt;</c>.</summary>
    private static SyntaxNode RewriteQualifier(SyntaxNode node, string newNs) => node switch
    {
        QualifiedNameSyntax qn => qn.WithLeft(SyntaxFactory.ParseName(newNs)),
        MemberAccessExpressionSyntax ma => ma.WithExpression(SyntaxFactory.ParseExpression(newNs)),
        _ => node,
    };

    /// <summary>Add a <c>using &lt;ns&gt;;</c> to the file at <paramref name="path"/> if absent.</summary>
    private static async Task<Solution> AddUsingAsync(Solution sol, string path, string ns, CancellationToken ct)
    {
        var doc = sol.Projects.SelectMany(p => p.Documents)
                     .FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (doc is null) return sol;
        var root = await doc.GetSyntaxRootAsync(ct);
        if (root is not CompilationUnitSyntax cu) return sol;
        if (cu.Usings.Any(u => u.Name?.ToString() == ns)) return sol;
        return doc.WithSyntaxRoot(cu.AddUsings(MakeUsing(ns))).Project.Solution;
    }

    /// <summary>A properly-spaced <c>using &lt;ns&gt;;</c> directive on its own line. Raw
    /// <see cref="SyntaxFactory.UsingDirective"/> has no trivia (renders <c>usingNs;</c>), and added
    /// directives sit in files cs4ai doesn't canonicalize, so format them here.</summary>
    private static UsingDirectiveSyntax MakeUsing(string ns) =>
        SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns))
            .NormalizeWhitespace()
            .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

    /// <summary>The single declaring file of <paramref name="sym"/>, or — for a partial type spread
    /// across files — the one matching <paramref name="inFile"/> (null if ambiguous and none given).</summary>
    private static string? PickFilePath(ISymbol sym, string? inFile)
    {
        var paths = sym.DeclaringSyntaxReferences
            .Select(r => r.SyntaxTree.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0) return null;
        if (paths.Count == 1) return paths[0];
        if (inFile is null) return null;
        var norm = inFile.Replace('\\', '/');
        return paths.FirstOrDefault(p => p!.Replace('\\', '/').EndsWith(norm, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Parse a <c>--set-usings</c> value into directives. Full <c>using</c> statements pass
    /// through verbatim (handles <c>using static</c>/<c>global using</c>/aliases/generics); a bare
    /// comma/newline/semicolon list becomes <c>using X;</c> per entry.</summary>
    private static (SyntaxList<UsingDirectiveSyntax> usings, Cs4AiResult? error) ParseUsings(string text)
    {
        text = text.Trim();
        string source = text.Contains("using", StringComparison.Ordinal)
            ? text
            : string.Join("\n", text
                .Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => $"using {p};"));

        var cu = SyntaxFactory.ParseCompilationUnit(source);
        if (cu.ContainsDiagnostics || cu.Usings.Count == 0)
            return (default, Cs4AiResult.UsageError(
                $"--set-usings: could not parse '{text}' — expected namespaces or using statements."));
        return (cu.Usings, null);
    }

    private async Task<(Solution, Cs4AiResult?, IReadOnlyList<Restate>)> ApplyRenameAsync(
        Solution sol, Operation op, CancellationToken ct)
    {
        var (resolved, err) = await AddressResolver.ResolveAsync(sol, op.Source!, ct);
        if (err is { } e) return (sol, e, NoRestate);
        var sym = resolved.Symbol;
        if (sym is null) return (sol, Cs4AiResult.NotFound($"address not found: '{op.Source}'"), NoRestate);
        var containingType = sym as INamedTypeSymbol ?? sym.ContainingType;
        if (containingType is null)
            return (sol, Cs4AiResult.UsageError($"rename: cannot determine containing type for '{op.Source}'"), NoRestate);

        var oldAddr = AddressResolver.Render(sym); // before the rename
        var newSol = await Renamer.RenameSymbolAsync(sol, sym, new SymbolRenameOptions(), op.Destination!, ct);
        var shapeDocId = sym is INamedTypeSymbol
            ? RenamedDocId(containingType, op.Destination!)
            : containingType.GetDocumentationCommentId();

        // The cascade is otherwise invisible: the frame shows the declaration, not the call-site
        // files the rename rewrote (found live: a rename rewrote a test file with no visible trace).
        var declPaths = sym.DeclaringSyntaxReferences
            .Select(r => r.SyntaxTree.FilePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cascadeFiles = new List<string>();
        var cascadeRefs = 0;
        var touchedPaths = new HashSet<string>(declPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var pc in newSol.GetChanges(sol).GetProjectChanges())
            foreach (var did in pc.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                var newDoc = newSol.GetDocument(did);
                var p = newDoc?.FilePath;
                if (p is null) continue;
                touchedPaths.Add(p);
                if (declPaths.Contains(p)) continue;
                cascadeFiles.Add(_relativize(p));
                // One text change ≈ one rewritten reference — the unit discover calls "refs".
                if (sol.GetDocument(did) is { } oldDoc)
                    cascadeRefs += (await newDoc!.GetTextChangesAsync(oldDoc, ct)).Count();
            }
        var cascadeNote = cascadeFiles.Count > 0
            ? $"references-rewritten: {cascadeRefs} ref{(cascadeRefs == 1 ? "" : "s")} across " +
              $"{cascadeFiles.Count} other file{(cascadeFiles.Count == 1 ? "" : "s")} · " +
              string.Join(", ", cascadeFiles.OrderBy(p => p, StringComparer.Ordinal))
            : null;

        // Prose can't be rewritten safely, but it CAN be reported: comment blocks in the touched
        // files that still say the old name (crefs were rewritten above — anything left is prose).
        var staleNote = await StaleCommentNoteAsync(newSol, touchedPaths, sym.Name, ct);

        // ── rename + --set-file: relocate the renamed type's file in the same command; one frame. ──
        if (op.File is not null)
        {
            if (sym is not INamedTypeSymbol)
                return (newSol, Cs4AiResult.UsageError(
                    "rename --set-file: addresses a type; relocate members with `move`."), NoRestate);
            var renamed = shapeDocId is not null ? await FindTypeByDocIdAsync(newSol, shapeDocId, ct) : null;
            if (renamed is null)
                return (newSol, Cs4AiResult.FileError("rename --set-file: renamed type not found."), NoRestate);
            var (msol, mErr, mRestate) = await ApplySetFileAsync(newSol, renamed, op.File, op.InFile, Ops.Rename, ct);
            if (mErr is { } me) return (newSol, me, NoRestate);
            var combined = $"{oldAddr} -> {op.Destination}" + (mRestate?.Delta is { } d ? $" · {d}" : "");
            return (msol, null, [new Restate(Ops.Rename, shapeDocId, FullBody: false, combined,
                JoinNotes(cascadeNote, staleNote, mRestate?.Note))]);
        }

        // ── reconcile hint: renaming a type whose (single-type) file no longer matches its name.
        //    Nudge, don't act — a file with siblings legitimately matches no single type, so no hint. ──
        string? hint = null;
        if (sym is INamedTypeSymbol && shapeDocId is not null)
        {
            var renamed = await FindTypeByDocIdAsync(newSol, shapeDocId, ct);
            var dref = renamed is null ? null : PickDeclaration(renamed, null); // null when partial
            if (dref?.SyntaxTree.FilePath is { } fp
                && TopLevelTypeCount(await dref.SyntaxTree.GetRootAsync(ct)) <= 1
                && !string.Equals(Path.GetFileNameWithoutExtension(fp), op.Destination, StringComparison.Ordinal))
                hint = $"note: {op.Destination} now lives in {Path.GetFileName(fp)} · " +
                       $"reconcile with --set-file \"{op.Destination}.cs\"";
        }

        // Delta body — only the name changed; the frame carries the fresh token.
        return (newSol, null, [new Restate(Ops.Rename, shapeDocId, FullBody: false,
            $"{oldAddr} -> {op.Destination}", JoinNotes(cascadeNote, staleNote, hint))]);
    }

    /// <summary>Whole-word mentions of <paramref name="oldName"/> in comment trivia of the touched
    /// files, reported as begin-to-end line blocks (consecutive <c>//</c> lines merge) so a text
    /// edit can operate on the whole block — cs4ai has no verb for plain comments. Doc-comment
    /// blocks count too: their crefs were just rewritten, so a remaining mention is prose
    /// (fixable semantically via update --set-comment).</summary>
    private async Task<string?> StaleCommentNoteAsync(
        Solution sol, IReadOnlyCollection<string> touchedPaths, string oldName, CancellationToken ct)
    {
        if (touchedPaths.Count == 0 || oldName.Length == 0) return null;
        var word = new Regex($@"\b{Regex.Escape(oldName)}\b", RegexOptions.CultureInvariant);

        var blocks = new List<(string file, int start, int end, bool hit)>();
        foreach (var doc in sol.Projects.SelectMany(p => p.Documents))
        {
            if (doc.FilePath is not { } fp || !touchedPaths.Contains(fp)) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root is null) continue;
            var text = await doc.GetTextAsync(ct);

            // Every comment trivia, hit or not — consecutive lines merge into ONE block first, and
            // a mention anywhere flags the whole block (the report's consumer is a whole-block edit).
            var pieces = new List<(int start, int end, bool hit)>();
            foreach (var trivia in root.DescendantTrivia())
            {
                if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                    && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                    && !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                    && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)) continue;
                var start = text.Lines.GetLinePosition(trivia.SpanStart).Line + 1;
                var end = text.Lines.GetLinePosition(Math.Max(trivia.Span.End - 1, trivia.SpanStart)).Line + 1;
                pieces.Add((start, end, word.IsMatch(trivia.ToFullString())));
            }

            foreach (var (start, end, hit) in pieces.OrderBy(p => p.start))
            {
                if (blocks.Count > 0 && blocks[^1].file == fp && start <= blocks[^1].end + 1)
                    blocks[^1] = (fp, blocks[^1].start, Math.Max(blocks[^1].end, end), blocks[^1].hit || hit);
                else
                    blocks.Add((fp, start, end, hit));
            }
        }

        blocks.RemoveAll(b => !b.hit);
        if (blocks.Count == 0) return null;
        const int cap = 12;
        var shown = blocks.Take(cap)
            .Select(b => $"{_relativize(b.file)}:{(b.start == b.end ? $"{b.start}" : $"{b.start}-{b.end}")}");
        var overflow = blocks.Count > cap ? $" (+{blocks.Count - cap} more)" : "";
        return $"comments-mention-old-name: {string.Join(" · ", shown)}{overflow} — prose still says '{oldName}'";
    }

    /// <summary>Stack note lines under one frame (each renders as its own line).</summary>
    private static string? JoinNotes(params string?[] notes)
    {
        var present = notes.Where(n => !string.IsNullOrEmpty(n)).ToList();
        return present.Count == 0 ? null : string.Join("\n", present);
    }

    private async Task<(Solution, Cs4AiResult?, IReadOnlyList<Restate>)> ApplyDeleteAsync(
        Solution sol, Operation op, CancellationToken ct)
    {
        var (resolved, err) = await AddressResolver.ResolveAsync(sol, op.Source!, ct);
        if (err is { } e) return (sol, e, NoRestate);
        var sym = resolved.Symbol;
        if (sym is null) return (sol, Cs4AiResult.NotFound($"address not found: '{op.Source}'"), NoRestate);
        var containingType = sym as INamedTypeSymbol ?? sym.ContainingType;
        if (containingType is null)
            return (sol, Cs4AiResult.UsageError($"delete: cannot determine containing type for '{op.Source}'"), NoRestate);

        var deletedAddr = AddressResolver.Render(sym); // before removal
        bool wholeType = ReferenceEquals(sym, containingType);
        foreach (var declRef in sym.DeclaringSyntaxReferences)
        {
            var doc = sol.GetDocument(declRef.SyntaxTree);
            if (doc is null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root is null) continue;
            var node = root.FindNode(declRef.Span);
            var memberNode = node as MemberDeclarationSyntax
                          ?? node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            if (memberNode is null) continue;
            var newRoot = root.RemoveNode(memberNode, SyntaxRemoveOptions.KeepNoTrivia)!;
            sol = doc.WithSyntaxRoot(newRoot).Project.Solution;
        }
        // Whole type → bare line (nothing left to frame); member → frame the containing type.
        return (sol, null, wholeType
            ? [new Restate(Ops.Delete, null, false, $"deleted: {deletedAddr}")]
            : [new Restate(Ops.Delete, containingType.GetDocumentationCommentId(), false, $"deleted: {deletedAddr}")]);
    }

    private async Task<(Solution, Cs4AiResult?, IReadOnlyList<Restate>)> ApplyMoveAsync(
        Solution sol, Operation op, CancellationToken ct)
    {
        var (resolvedMember, mErr) = await AddressResolver.ResolveAsync(sol, op.Source!, ct);
        if (mErr is { } me) return (sol, me, NoRestate);
        if (resolvedMember.Symbol is null || resolvedMember.Symbol.ContainingType is null)
            return (sol, Cs4AiResult.NotFound($"member not found: '{op.Source}'"), NoRestate);

        var (resolvedDest, dErr) = await AddressResolver.ResolveAsync(sol, op.Destination!, ct);
        if (dErr is { } de) return (sol, de, NoRestate);
        if (resolvedDest.Symbol is not INamedTypeSymbol)
            return (sol, Cs4AiResult.UsageError($"move: target '{op.Destination}' is not a type."), NoRestate);

        var sourceType = resolvedMember.Symbol.ContainingType;
        var memberAddr = AddressResolver.Render(resolvedMember.Symbol); // before the move
        var memberDecl = resolvedMember.Symbol.DeclaringSyntaxReferences[0];
        var memberNode = (MemberDeclarationSyntax)await memberDecl.GetSyntaxAsync(ct);

        var sourceDoc = sol.GetDocument(memberDecl.SyntaxTree);
        if (sourceDoc is null) return (sol, Cs4AiResult.FileError("move: source document not found."), NoRestate);
        var sourceRoot = await memberDecl.SyntaxTree.GetRootAsync(ct);
        var newSourceRoot = sourceRoot.RemoveNode(memberNode, SyntaxRemoveOptions.KeepNoTrivia)!;
        sol = sourceDoc.WithSyntaxRoot(newSourceRoot).Project.Solution;

        var (destRe, destReErr) = await AddressResolver.ResolveAsync(sol, op.Destination!, ct);
        if (destReErr is not null || destRe.Symbol is not INamedTypeSymbol destNow)
            return (sol, Cs4AiResult.FileError("move: target type lost during staging."), NoRestate);

        var destDeclRef = PickDeclaration(destNow, op.InFile);
        if (destDeclRef is null)
            return (sol, Cs4AiResult.UsageError(
                $"move: target '{destNow.Name}' is partial; --in-file required."), NoRestate);
        var destDoc = sol.GetDocument(destDeclRef.SyntaxTree);
        if (destDoc is null) return (sol, Cs4AiResult.FileError("move: target document not found."), NoRestate);
        var destRoot = await destDeclRef.SyntaxTree.GetRootAsync(ct);
        var destNode = (TypeDeclarationSyntax)await destDeclRef.GetSyntaxAsync(ct);
        var newDestRoot = destRoot.ReplaceNode(destNode, destNode.AddMembers(memberNode));
        sol = destDoc.WithSyntaxRoot(newDestRoot).Project.Solution;

        var target = AddressResolver.Render(destNow);
        var source = AddressResolver.Render(sourceType);
        // Both types tick — frame both with a delta.
        return (sol, null,
        [
            new Restate(Ops.Move, sourceType.GetDocumentationCommentId(), false, $"moved out: {memberAddr} -> {target}"),
            new Restate(Ops.Move, destNow.GetDocumentationCommentId(), false, $"moved in: {memberAddr} <- {source}"),
        ]);
    }

    /// <summary>
    /// <c>--set-file</c> — reconcile a <b>type</b> with the file it lives in. Intra-project only
    /// (relative to the owning project's directory; no escape, no collision). One concept, two
    /// mechanics reported in the frame: if the type is <i>alone</i> in its source file the whole file
    /// is renamed (<c>git mv</c> when tracked → history preserved; plain rename otherwise); if the
    /// file holds <i>other</i> types the type is <i>extracted</i> into the new file and the siblings
    /// stay (git sees an add + a shrink — the moved type's history splits). Returns a delta
    /// <see cref="Restate"/> framing the type with its fresh token.
    /// </summary>
    private async Task<(Solution sol, Cs4AiResult? error, Restate? restate)> ApplySetFileAsync(
        Solution sol, INamedTypeSymbol type, string targetSpec, string? inFile, string op, CancellationToken ct)
    {
        var declRef = PickDeclaration(type, inFile);
        if (declRef is null)
            return (sol, Cs4AiResult.UsageError(
                $"--set-file: '{type.Name}' is partial; --in-file required to pick which file to move."), null);

        var oldPath = declRef.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(oldPath))
            return (sol, Cs4AiResult.FileError("--set-file: the source declaration has no file on disk."), null);
        var doc = sol.GetDocument(declRef.SyntaxTree);
        if (doc is null) return (sol, Cs4AiResult.FileError("--set-file: source document not found."), null);
        var project = doc.Project;

        // Resolve + guard the target: relative to the project folder, may nest into subfolders, must
        // stay inside the project tree, must not collide, must actually differ.
        var projDir = Path.GetDirectoryName(project.FilePath) ?? Directory.GetCurrentDirectory();
        var spec = targetSpec.Replace('\\', '/').Trim();
        if (!spec.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) spec += ".cs";
        var newPath = Path.GetFullPath(Path.Combine(projDir, spec));
        var rel = Path.GetRelativePath(projDir, newPath);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            return (sol, Cs4AiResult.UsageError(
                $"--set-file: '{targetSpec}' escapes the project folder — moves are intra-project only."), null);
        if (string.Equals(Path.GetFullPath(oldPath), newPath, StringComparison.OrdinalIgnoreCase))
            return (sol, Cs4AiResult.UsageError(
                $"--set-file: '{type.Name}' already lives in {_relativize(newPath)}."), null);

        var leaf = Path.GetFileName(newPath);
        var relDir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
        var folders = relDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var root = await declRef.SyntaxTree.GetRootAsync(ct);
        var typeNode = await declRef.GetSyntaxAsync(ct);
        var typeNs = type.ContainingNamespace is { IsGlobalNamespace: false } cns ? cns.ToDisplayString() : "";
        var alone = TopLevelTypeCount(root) <= 1;
        var moveDelta = $"{_relativize(oldPath)} -> {_relativize(newPath)}";

        // Does the target already exist? An occupied target is NOT a collision — co-locate the type
        // into it (append), removing it from the source (delete the source file when the type was
        // alone there). Same "which file should this type live in" rule as create --in-file.
        var targetDoc = sol.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath is { } fp
                && string.Equals(Path.GetFullPath(fp), newPath, StringComparison.OrdinalIgnoreCase));
        if (targetDoc is null && File.Exists(newPath))
            return (sol, Cs4AiResult.UsageError(
                $"--set-file: '{_relativize(newPath)}' exists on disk but is not part of the project."), null);

        if (targetDoc is not null)
        {
            var targetId = targetDoc.Id;
            if (alone) sol = sol.RemoveDocument(doc.Id);   // source becomes empty → drop the file
            else sol = doc.WithSyntaxRoot(root.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia)!).Project.Solution;

            var (coSol, coErr) = await CoLocateTypeAsync(sol, targetId, (MemberDeclarationSyntax)typeNode, typeNs, ct);
            if (coErr is { } ce) return (sol, ce, null);
            sol = coSol;

            var note = alone
                ? $"note: {_relativize(oldPath)} removed — the type was alone there"
                : $"note: extracted from {_relativize(oldPath)} — history splits (git sees a new file)";
            return (sol, null, new Restate(op, type.GetDocumentationCommentId(), FullBody: false,
                $"co-located into {_relativize(newPath)}", note));
        }

        if (alone)
        {
            // ── Alone → target doesn't exist: rename the whole file. git mv when it's tracked (history
            //    preserved); the in-memory doc moves either way and write-through reconciles disk. ──
            var text = (await doc.GetTextAsync(ct)).ToString();
            sol = sol.RemoveDocument(doc.Id);
            var proj2 = sol.GetProject(project.Id)!;
            var newDoc = proj2.AddDocument(leaf, text, folders.Length > 0 ? folders : null, newPath);
            sol = newDoc.Project.Solution;

            var gitMoved = await GitCli.TryMoveAsync(Path.GetFullPath(oldPath), newPath, ct);
            var note = gitMoved
                ? "note: git mv — file renamed, history preserved"
                : "note: file renamed (not git-tracked — history via content match on commit)";
            return (sol, null, new Restate(op, type.GetDocumentationCommentId(), FullBody: false,
                $"{moveDelta} ({(gitMoved ? "git mv" : "renamed")})", note));
        }

        // ── Shared → target doesn't exist: extract just this type into the new file; siblings stay. No
        //    git mv — git scores this an add + a shrink, so the moved type's history can't be a rename. ──
        var siblings = TopLevelTypeCount(root) - 1;
        sol = doc.WithSyntaxRoot(root.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia)!).Project.Solution;

        var cu = root as CompilationUnitSyntax;
        var usingsText = cu is null ? "" : cu.Usings.ToFullString().Trim();
        var fileText =
            (usingsText.Length > 0 ? usingsText + "\n\n" : "") +
            (typeNs.Length > 0 ? $"namespace {typeNs};\n\n" : "") +
            typeNode.ToFullString().Trim() + "\n";

        var proj3 = sol.GetProject(project.Id)!;
        var extracted = proj3.AddDocument(leaf, fileText, folders.Length > 0 ? folders : null, newPath);
        sol = extracted.Project.Solution;

        return (sol, null, new Restate(op, type.GetDocumentationCommentId(), FullBody: false,
            $"extracted into {_relativize(newPath)}",
            $"note: {_relativize(oldPath)} keeps {siblings} other type(s) — history splits (git sees a new file)"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Count of <b>top-level</b> type-like declarations in a file (classes/structs/records/
    /// interfaces/enums/delegates), not descending into type bodies — so nested types don't inflate
    /// the "is this type alone in its file?" decision that <c>--set-file</c> forks on.</summary>
    private static int TopLevelTypeCount(SyntaxNode root) =>
        root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .Count(n => n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);

    /// <summary>Append a type declaration into an existing file — the shared "place this type in file X"
    /// step for both <c>create --in-file</c> (a new type) and <c>--set-file</c> into an occupied target
    /// (a moved type). The target file's declared namespace must equal <paramref name="ns"/> (else a
    /// usage error — co-locating under a different namespace would contradict the FQN). Keyed by
    /// <see cref="DocumentId"/> so it operates on the <i>current</i> solution after any source edit.</summary>
    private static async Task<(Solution sol, Cs4AiResult? error)> CoLocateTypeAsync(
        Solution sol, DocumentId targetId, MemberDeclarationSyntax type, string ns, CancellationToken ct)
    {
        var targetDoc = sol.GetDocument(targetId);
        if (targetDoc is null) return (sol, Cs4AiResult.FileError("co-locate: target document not found."));
        if (await targetDoc.GetSyntaxRootAsync(ct) is not CompilationUnitSyntax cu)
            return (sol, Cs4AiResult.FileError($"co-locate: '{targetDoc.Name}' is not a compilation unit."));

        var fsn = cu.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        var bn = cu.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        var fileNs = fsn?.Name.ToString() ?? bn?.Name.ToString() ?? "";
        if (!string.Equals(fileNs, ns, StringComparison.Ordinal))
            return (sol, Cs4AiResult.UsageError(
                $"co-locate: '{Path.GetFileName(targetDoc.FilePath)}' is namespace " +
                $"'{(fileNs.Length == 0 ? "<global>" : fileNs)}', but the type is '{(ns.Length == 0 ? "<global>" : ns)}' — " +
                "namespaces must match to place a type in a file."));

        // Same name + same arity already declared here → appending would be a duplicate (only legal
        // when both sides are partial). Different arity (Result vs Result<T>) is fine side by side.
        var (newName, newArity) = TypeNameArity(type);
        var siblings = fsn?.Members ?? bn?.Members ?? cu.Members;
        var dup = siblings.FirstOrDefault(m => TypeNameArity(m) == (newName, newArity));
        if (dup is not null &&
            !(type.Modifiers.Any(SyntaxKind.PartialKeyword) && dup.Modifiers.Any(SyntaxKind.PartialKeyword)))
            return (sol, Cs4AiResult.UsageError(
                $"create: '{Path.GetFileName(targetDoc.FilePath)}' already declares " +
                $"'{newName}{(newArity > 0 ? $"<{newArity}>" : "")}' — use `update` to change it or `delete` first."));

        // Prepend a blank line so the appended type is separated; keep its own leading trivia
        // (doc comments). Canonicalization of the framed type normalizes the elastic spacing.
        var spaced = type.WithLeadingTrivia(
            SyntaxFactory.TriviaList(SyntaxFactory.ElasticCarriageReturnLineFeed, SyntaxFactory.ElasticCarriageReturnLineFeed)
                .AddRange(type.GetLeadingTrivia()));

        SyntaxNode newRoot =
              fsn is not null ? cu.ReplaceNode(fsn, fsn.AddMembers(spaced))
            : bn  is not null ? cu.ReplaceNode(bn, bn.AddMembers(spaced))
            : cu.AddMembers(spaced); // global namespace (ns == "")

        return (targetDoc.WithSyntaxRoot(newRoot).Project.Solution, null);
    }

    /// <summary>Identifier + generic arity of a type-shaped member; ("", -1) for non-types so they
    /// never collide with a real name.</summary>
    private static (string name, int arity) TypeNameArity(MemberDeclarationSyntax m) => m switch
    {
        TypeDeclarationSyntax t => (t.Identifier.Text, t.TypeParameterList?.Parameters.Count ?? 0),
        DelegateDeclarationSyntax d => (d.Identifier.Text, d.TypeParameterList?.Parameters.Count ?? 0),
        BaseTypeDeclarationSyntax b => (b.Identifier.Text, 0), // enum
        _ => ("", -1),
    };

    /// <summary>C#-spelled name of a type-shaped declaration, e.g. <c>Result&lt;T&gt;</c>.</summary>
    private static string RenderTypeDecl(MemberDeclarationSyntax m) => m switch
    {
        TypeDeclarationSyntax t => t.Identifier.Text + (t.TypeParameterList?.ToString() ?? ""),
        DelegateDeclarationSyntax d => d.Identifier.Text + (d.TypeParameterList?.ToString() ?? ""),
        BaseTypeDeclarationSyntax b => b.Identifier.Text,
        _ => m.Kind().ToString(),
    };

    private static SyntaxReference? PickDeclaration(INamedTypeSymbol type, string? inFile)
    {
        foreach (var declRef in type.DeclaringSyntaxReferences)
        {
            if (inFile is null)
            {
                if (type.DeclaringSyntaxReferences.Length == 1) return declRef;
                return null; // partial + no --in-file → caller errors
            }
            var path = (declRef.SyntaxTree.FilePath ?? "").Replace('\\', '/');
            if (path.EndsWith(inFile.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) return declRef;
        }
        return inFile is null ? type.DeclaringSyntaxReferences.FirstOrDefault() : null;
    }

    /// <summary>Longest-prefix namespace match: the project whose default namespace is the longest
    /// prefix of <paramref name="ns"/>. Falls back to the sole project when nothing matches.</summary>
    private static Project? PickProjectByNamespace(Solution sol, string ns)
    {
        Project? best = null;
        int bestLen = -1;
        foreach (var p in sol.Projects)
        {
            var root = p.DefaultNamespace ?? p.AssemblyName ?? p.Name;
            if (string.IsNullOrEmpty(root)) continue;
            bool match = string.Equals(ns, root, StringComparison.Ordinal)
                      || ns.StartsWith(root + ".", StringComparison.Ordinal);
            if (match && root.Length > bestLen) { best = p; bestLen = root.Length; }
        }
        if (best is not null) return best;
        var all = sol.Projects.ToList();
        return all.Count == 1 ? all[0] : null;
    }

    private static async Task<(Solution sol, IReadOnlyList<string> added)> InferUsingsAsync(
        Solution sol, DocumentId docId, CancellationToken ct)
    {
        try
        {
            var doc = sol.GetDocument(docId);
            if (doc is null) return (sol, []);
            var model = await doc.GetSemanticModelAsync(ct);
            var root = await doc.GetSyntaxRootAsync(ct);
            var comp = await doc.Project.GetCompilationAsync(ct);
            if (model is null || root is not CompilationUnitSyntax cu || comp is null) return (sol, []);

            var unresolved = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in model.GetDiagnostics(null, ct))
            {
                if (d.Id is not ("CS0246" or "CS0103" or "CS0234")) continue;
                var node = root.FindNode(d.Location.SourceSpan);
                var name = (node as IdentifierNameSyntax)?.Identifier.Text
                        ?? node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().FirstOrDefault()?.Identifier.Text;
                if (!string.IsNullOrEmpty(name)) unresolved.Add(name!);
            }
            if (unresolved.Count == 0) return (sol, []);

            var existing = cu.Usings.Select(u => u.Name?.ToString()).Where(n => n is not null).ToHashSet();
            var toAdd = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var name in unresolved)
            {
                var namespaces = new List<string>();
                CollectNamespacesWithType(comp.GlobalNamespace, name, namespaces);
                var distinct = namespaces.Distinct().Where(n => n is { Length: > 0 }).ToList();
                if (distinct.Count == 1 && !existing.Contains(distinct[0])) toAdd.Add(distinct[0]!);
            }
            if (toAdd.Count == 0) return (sol, []);

            var newCu = cu.AddUsings(toAdd.Select(MakeUsing).ToArray());
            return (doc.WithSyntaxRoot(newCu).Project.Solution, toAdd.ToList());
        }
        catch { return (sol, []); } // inference is best-effort; build surfaces any remaining gaps
    }

    private static void CollectNamespacesWithType(INamespaceSymbol ns, string typeName, List<string> sink)
    {
        foreach (var t in ns.GetTypeMembers())
            if (string.Equals(t.Name, typeName, StringComparison.Ordinal)) sink.Add(ns.ToDisplayString());
        foreach (var child in ns.GetNamespaceMembers())
            CollectNamespacesWithType(child, typeName, sink);
    }

    /// <summary>Parse a <c>--set-attributes</c> value (<c>[A],[B]</c>) into attribute lists. The
    /// comma between lists becomes whitespace (C# separates attribute lists by whitespace, not
    /// commas); each <c>[…]</c> is one list. Best-effort split on <c>],[</c> — an attribute argument
    /// containing that exact literal is a pathological miss.</summary>
    private static (SyntaxList<AttributeListSyntax> lists, Cs4AiResult? error) ParseAttributeLists(string text)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(text, @"\]\s*,\s*\[", "] [");
        var probe = SyntaxFactory.ParseMemberDeclaration(normalized + "\nvoid __cs4ai_attrs__() {}");
        if (probe is null || probe.ContainsDiagnostics || probe.AttributeLists.Count == 0)
            return (default, Cs4AiResult.UsageError(
                $"--set-attributes: could not parse '{text}' — expected attribute lists like [A],[B]."));
        return (probe.AttributeLists, null);
    }

    private static MemberDeclarationSyntax WithDocComment(SyntaxNode node, string xml)
    {
        var lines = xml.Replace("\r\n", "\n").Split('\n');
        var trivia = new List<SyntaxTrivia>();
        foreach (var line in lines)
        {
            var text = line.TrimStart();
            if (!text.StartsWith("///", StringComparison.Ordinal)) text = "/// " + text;
            trivia.Add(SyntaxFactory.Comment(text));
            trivia.Add(SyntaxFactory.ElasticCarriageReturnLineFeed);
        }
        var member = (MemberDeclarationSyntax)node;
        var kept = member.GetLeadingTrivia()
            .Where(t => !t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                     && !(t.IsKind(SyntaxKind.SingleLineCommentTrivia) && t.ToString().StartsWith("///", StringComparison.Ordinal)));
        return member.WithLeadingTrivia(SyntaxFactory.TriviaList(trivia).AddRange(kept));
    }

    private static string StripGenerics(string name)
    {
        var lt = name.IndexOf('<');
        if (lt >= 0) return name[..lt];
        var tick = name.IndexOf('`');
        return tick >= 0 ? name[..tick] : name;
    }

    private static int ArityOf(string leaf)
    {
        var lt = leaf.IndexOf('<');
        if (lt < 0) { var tk = leaf.IndexOf('`'); return tk < 0 ? 0 : int.TryParse(leaf[(tk + 1)..], out var n) ? n : 0; }
        return leaf[lt..].Count(c => c == ',') + 1;
    }

    private static string? RenamedDocId(INamedTypeSymbol type, string newName)
    {
        var docId = type.GetDocumentationCommentId();
        if (docId is null) return null;
        var lastDot = docId.LastIndexOf('.');
        var tail = docId[(lastDot + 1)..];
        var tick = tail.IndexOf('`');
        var aritySuffix = tick >= 0 ? tail[tick..] : "";
        return lastDot < 0 ? $"T:{newName}{aritySuffix}" : $"{docId[..(lastDot + 1)]}{newName}{aritySuffix}";
    }

    private async Task<(Solution, INamedTypeSymbol?)> CanonicalizeAsync(
        Solution sol, INamedTypeSymbol type, CancellationToken ct)
    {
        var docId = type.GetDocumentationCommentId();
        foreach (var declRef in type.DeclaringSyntaxReferences)
        {
            var doc = sol.GetDocument(declRef.SyntaxTree);
            if (doc is null) continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root is null) continue;
            var node = root.FindNode(declRef.Span);
            var typeNode = node as TypeDeclarationSyntax
                        ?? node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (typeNode is null) continue;
            var canonical = Canonicalizer.Apply(typeNode, doc.Project.Solution.Workspace, _config);
            if (ReferenceEquals(canonical, typeNode)) continue;
            var newRoot = root.ReplaceNode(typeNode, canonical);
            sol = doc.WithSyntaxRoot(newRoot).Project.Solution;
        }
        var refreshed = docId is null ? null : await FindTypeByDocIdAsync(sol, docId, ct);
        return (sol, refreshed);
    }

    private static async Task<INamedTypeSymbol?> FindTypeByDocIdAsync(
        Solution sol, string docId, CancellationToken ct)
    {
        foreach (var project in sol.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;
            var sym = DocumentationCommentId.GetFirstSymbolForDeclarationId(docId, compilation);
            if (sym is INamedTypeSymbol nt) return nt;
        }
        return null;
    }
}
