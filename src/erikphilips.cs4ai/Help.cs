namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Help text + the embedded Claude Code skill. Kept here so the engine file stays self-contained
/// (matches md4ai's pattern).
/// <para>
/// <see cref="SkillFile"/> is the single most behavior-determining artifact cs4ai ships, because
/// it's what the agent actually reads. Changes to its prose require the same care as changes to
/// the token recipe — see version2.md, <i>Help + skill</i>. Reference-card discipline: command /
/// effect / recovery lines, no rationale.
/// </para>
/// </summary>
internal static class Help
{
    public static string For(string verb) => verb switch
    {
        "session"          => Session,
        "inspect"          => Inspect,
        "discover"         => Discover,
        "create"           => Create,
        "update"           => Update,
        "rename"           => Rename,
        "delete"           => Delete,
        "move"             => Move,
        "create-project"   => CreateProject,
        "update-project"   => UpdateProject,
        "delete-project"   => DeleteProject,
        "add-reference"    => AddReference,
        "delete-reference" => DeleteReference,
        "build"            => Build,
        "run-test"         => RunTest,
        "verify"           => Verify,
        "init"             => Init,
        "stop-daemon"      => StopDaemon,
        "reload"           => Reload,
        _                  => Top,
    };

    public const string Top = """
        cs4ai — semantic editor for C# code, backed by Roslyn. Built for AI agents.

        Usage: cs4ai <verb> <session-token> [args] [flags]

        Start with `session`; every other verb leads with the session token it returns (`sess_…`).
        A session is a WORK-SPAN, not a transaction: edits write to disk immediately. There is no
        commit/discard — undo is git's job. Run `verify` when done for the authoritative build+test.

        Bootstrap:
          session [<solution-path>] [--format sln|slnx] [--log] [--debug]
                                              open-or-create (path defaults to cwd); runs a full build
                                              and returns the opening buildOutcome (where you stand)

        Reads (lead with the sess_ token):
          inspect  <sess> <address>     one type, whole, framed, XML comments (eats a raw stack frame)
          discover <sess> <name>        breadth: census + Referenced by (incoming) / References (outgoing)

        Edits (write through to disk immediately; each carries a trailing [build …] block — CS only):
          create <sess> <new-fqn>   --set-body <decl> [--path <dir>] [--in-file <f>]
          update <sess> <address>   --token <type_…> [--set-body|--set-comment|--set-namespace|--set-usings]
          rename <sess> <address> <new-name>   --token <type_…>     (cascades)
          move   <sess> <member> <target-type> --token <type_…> [--in-file <f>]   (cascades)
          delete <sess> <address>              --token <type_…>     (may cascade)
          Any multi-line body: write it to a file and use --from <file> (shell-safe; avoids quoting
          hangs). Inline --set-body is for trivial one-liners only. (--stdin reads stdin explicitly.)

        Structure / graph (immediate; write disk + full build + reload; report CS*+NU*+MSBuild):
          create-project   <sess> <name> --path <dir> [--template <t>]
          update-project   <sess> <project.csproj> --set-body <whole-xml>
          delete-project   <sess> <project.csproj>
          add-reference    <sess> <project.csproj> <package|ref.csproj> [--version <v>]
          delete-reference <sess> <project.csproj> <package|ref.csproj>

        End-of-work:
          build <sess>      fast Roslyn CS re-check          run-test <sess>   tests on disk
          verify <sess>     authoritative full build + tests (report, not a gate)

        Daemon & config:
          init <slnx> <preset>   write .cs4aiconfig (sa1201 | microsoft | source)
          reload <slnx>          invalidate the warm workspace after a git pull/checkout
          stop-daemon <slnx>     stop the per-solution daemon

        Meta: --version | --show-readme | --create-skill [<dir>] | <verb> --help

        Exit codes: 0 ok · 1 usage (unknown flag, forbidden field) · 2 file/parse ·
        3 ambiguous · 4 not found · 5 stale/missing token (body carries shape + fresh token) ·
        6 needs config · 7 no session. 5/6/7 are recoveries: the body carries what the retry needs.
        """;

    public const string Session = """
        cs4ai session [<solution-path>] [--format sln|slnx] [--log] [--debug]

        Open a work-span on the solution at the path (or create it if absent). The path defaults to the
        current directory: in a directory, cs4ai opens the one solution there, or creates <dirname>.sln
        when there is none (several → it lists them; name one). Creating writes the solution + a default
        .cs4aiconfig. Runs a full build at open and returns: the session token, solution: opened|created,
        the full solution path, and the opening buildOutcome (so you know exactly where you stand before
        touching anything). Lead with the token on every later call. Edits write through to disk
        immediately — no commit; undo is git.

        --format  the solution format when this session CREATES one (no-op when it opens an existing
                  solution): slnx (default — the modern XML format) or sln (classic). An explicit
                  extension in the path wins; contradicting it is a usage error.

        --log  record every command issued under this session (verbatim, with exit codes) and write
               the transcript to cs4ai_<token>.log in the solution directory on `verify`. Valid only
               on `session`.

        --debug  trace the daemon this command spawns (lifecycle, per-command begin/exit, unhandled
                 exceptions) to cs4ai-daemon.log in the solution directory. Accepted on any command,
                 but only takes effect when the command starts the daemon; an already-running daemon
                 keeps its mode. Without it, daemon diagnostics are discarded.
        """;

    public const string Inspect = """
        cs4ai inspect <sess-token> <address>

        One type, whole, framed by [address · path · lines · token]; XML doc comments included.
        <address> is an FQN (angle-form generics), file:line, or a raw stack frame (--from <file>
        for a whole trace). A member resolves up to its declaring type. Sees the live view.
        Cite the returned type_ token on edits to that type. A bare name matching multiple types shows
        all of them with an ambiguity note; an edit needs an unambiguous FQN (bare-name edit → exit 3).
        """;

    public const string Discover = """
        cs4ai discover <sess-token> <name-or-fqn>

        Breadth read: a category census (Types/Methods/Other, each with a [N] count) plus, for an
        unambiguous hit, "Referenced by" (incoming) and "References" (outgoing). Sees the live view.
        Use discover to map where a name lives and what touches it; inspect to read one type whole.
        """;

    public const string Create = """
        cs4ai create <sess-token> <new-fqn> --set-body <decl> [--path <dir>] [--in-file <file>]
            [--set-attributes '[A],[B]']

        A new member into a type, or a new top-level type into a project. The namespace comes from
        the FQN; --path is the folder INSIDE that project (project-relative — the project also comes
        from the FQN; a solution-root spelling like src/Proj/Common is auto-corrected with a note,
        and --path may not escape the project). The kind falls out of parsing --set-body — there is
        no --kind. --set-attributes attaches attributes (a whole set). A member into an existing type
        cites that type's --token <type_…>; a brand-new top-level type cites none. New files get
        file-scoped namespace + inferred usings (echoed back).
        --in-file <file> places the new type in that file: co-locates into an existing file (its
        namespace must match) or names a new one — it is honored, never silently dropped.
        """;

    public const string Update = """
        cs4ai update <sess-token> <address> --token <type_…> [facet]

        Replace a member — or a whole type — from its full declaration (--set-body); a type's name
        and arity must match the address (use `rename` to change the name). The engine diffs and
        cascades only if the signature changed, reporting which. Or change a facet: --set-comment <xml> (no cascade),
        --set-namespace <ns> (cascades the FQN), --set-usings <imports> (file imports),
        --set-attributes '[A],[B]' (whole-replace the attributes), --set-file <path> (the file a TYPE
        lives in — intra-project; renames the whole file via git mv when the type is alone, else
        extracts the type into a new file; --in-file picks a partial's source file). At least one is
        required. Large bodies via --from <file> or stdin.
        """;

    public const string Rename = """
        cs4ai rename <sess-token> <address> <new-name> --token <type_…> [--set-file <path>]

        Rename a member or type to a new bare name (not an FQN). Every reference rewrites, computed
        fresh from the live view — including doc-comment crefs. Plain-comment prose cannot rewrite
        safely, so blocks in the touched files still mentioning the old name are reported instead:
        `comments-mention-old-name: <file>:<start>-<end> · …` (full block extents, ready for a text
        edit; doc-comment prose is fixable with update --set-comment). --set-file relocates the
        renamed type's file in the same command (intra-project; git mv when the type is alone in its
        file, else extract; --in-file for partials). Renaming a type without --set-file, when its
        single-type file no longer matches, adds a reconcile hint. Writes through to disk immediately.
        """;

    public const string Move = """
        cs4ai move <sess-token> <member-address> <target-type> --token <type_…> [--in-file <file>]

        Relocate a member to another type, rewriting references. --in-file picks the file of a partial
        target. Writes through to disk immediately.
        """;

    public const string Delete = """
        cs4ai delete <sess-token> <address> --token <type_…>

        Remove a member or a whole type. May cascade (callers break — build will say so). Writes
        through to disk immediately. No body or facet flags (forbidden → exit 1).
        """;

    public const string CreateProject = """
        cs4ai create-project <sess-token> <name> --path <dir> [--template <t>]

        dotnet new <template> (default classlib) + dotnet sln add. Immediate (writes disk, full build,
        reload). Echoes the full .slnx + new .csproj + buildOutcome (CS*+NU*+MSBuild).
        """;

    public const string UpdateProject = """
        cs4ai update-project <sess-token> <project.csproj> --set-body <whole-xml>

        Replace the whole .csproj (the MSBuild escape hatch). Immediate (writes disk + reloads).
        Echoes the full .slnx + .csproj.
        """;

    public const string DeleteProject = """
        cs4ai delete-project <sess-token> <project.csproj>

        dotnet sln remove. Immediate (writes disk + reloads). Echoes the full .slnx.
        """;

    public const string AddReference = """
        cs4ai add-reference <sess-token> <project.csproj> <package-id|ref.csproj> [--version <v>]

        dotnet add reference (a .csproj) or package (an id; --version, "Latest" omits it). Immediate
        (writes disk, full build, reload). Echoes the full .slnx + .csproj + buildOutcome — a NU*/MSBuild
        problem the reference introduces (e.g. NU1510) shows here as `new`. (Provisional; `verify` is final.)
        """;

    public const string DeleteReference = """
        cs4ai delete-reference <sess-token> <project.csproj> <package-id|ref.csproj>

        dotnet remove reference/package. Immediate (writes disk + reloads). Echoes the .csproj.
        """;

    public const string Build = """
        cs4ai build <sess-token>

        Fast Roslyn CS re-check of the live view; returns the buildOutcome (CS* only — silent on
        NU*/MSBuild). Every edit already carries this as a trailing [build …] block; run `build` only
        to re-check without editing. For the full picture incl. NU*/MSBuild, run `verify`.
        """;

    public const string RunTest = """
        cs4ai run-test <sess-token>

        Run `dotnet test` on disk (edits already wrote through — no shadow copy); report new failures
        vs the session-open baseline + the full buildOutcome. Tests are skipped when the build fails.
        """;

    public const string Verify = """
        cs4ai verify <sess-token> [--raw]

        The end-of-work checkpoint: a mandatory full `dotnet build` + tests — the AUTHORITATIVE truth
        (CS*+NU*+MSBuild). A report, not a gate: edits are already on disk, so it refuses nothing and
        is always exit 0; read the buildOutcome rollup. Flushes the --log transcript if enabled.

        --raw  append the verbatim dotnet build + test transcript (restore chatter, target output)
               in a [raw dotnet output] fence after the report — for when the curated [build] block
               isn't enough.
        """;

    public const string Init = """
        cs4ai init <slnx-or-csproj> <preset>

        Write .cs4aiconfig at the repo root. Presets: sa1201 (recommended) | microsoft | source.
        Same preset on a configured repo -> exit 0 no-op; different -> exit 1 (retry your original
        command). A new solution's `session` already seeds sa1201, so init is for existing repos.
        """;

    public const string StopDaemon = """
        cs4ai stop-daemon <slnx-or-csproj>

        Stop the per-solution daemon: releases the config lock, drops the active session. The next call
        respawns it (cold load).
        """;

    public const string Reload = """
        cs4ai reload <slnx-or-csproj>

        Invalidate the warm workspace after a known external mutation (git checkout / pull / rebase).
        The next call re-parses from disk. Safe anytime — edits already wrote through, so disk is truth.
        """;

    // ─────────────────────────────────────────────────────────────────────────────
    //  The Claude Code skill. Reference-card prose (command / effect / recovery, no
    //  rationale). One taught path: session → lead with the token → edits cite --token
    //  from inspect; refusals are recovery, not the workflow. Emitted by
    //  `cs4ai --create-skill`; ships with the binary so it can't drift.
    // ─────────────────────────────────────────────────────────────────────────────

    public const string SkillFile = """
        ---
        name: cs4ai
        description: Semantic C# editor (CLI). Use INSTEAD OF Grep/Read/Edit for any C# symbol question or change — "where is X defined", "who calls X", "find/show me the class or method Y", read or edit a member by name, rename, change signatures, move members, resolve stack traces against source. Not for bulk text substitution or non-.cs files.
        ---
        # cs4ai (CLI)

        Shell is bash. Quote all paths (`"C:\repos\Foo\Foo.slnx"`).

        A session is a WORK-SPAN, not a transaction: edits write to disk immediately. There is no
        commit/discard — undo is git's job (`git checkout`/`restore`). Run `cs4ai verify` when done.

        Every command leads with a session token. Get one first:
        - `cs4ai session ["<…>/Foo.slnx"]` → runs a full build and returns `sess_…`, `solution: opened|created`,
          the full solution `path`, and the opening `buildOutcome` (where you stand before any edit).
          Lead with that token on every later call. The path is optional (defaults to cwd); an absent
          solution is created — as `.slnx` by default (`--format sln` for classic; an explicit
          extension in the path wins).
        - add `--log` to record the session's full transcript — every command, its exit code, and its
          output — to `cs4ai_<token>.log` (live-flushed; finalized on `verify`).
        - add `--debug` to a daemon-spawning command (usually `session`) to trace the daemon's
          lifecycle to `cs4ai-daemon.log` next to the solution — for diagnosing daemon behavior only.

        Addresses: FQN in C# spelling — `IRepository<T>`, `Foo.Foo(string)`, `Foo.this[int]`,
        `Baz(int,string)`. A `file.cs:42` (from a stack trace / build error) also resolves.

        Reads:
        - `cs4ai inspect <sess> <addr>` — one type, whole, with a `token: type_…`. Eats a raw stack frame
          (`cs4ai inspect <sess> --from trace.txt`). Cite that token to edit the type.
        - `cs4ai discover <sess> <name>` — census + who references it (incoming) / what it calls (outgoing).

        Edits (write through to disk immediately). Each cites `--token <type_…>` from an `inspect` of
        the target type (also echoed in every edit response):
        - `cs4ai create <sess> <new-fqn> --set-body <decl> [--path <dir>] [--in-file <file>]` — new member
          (cite the type's token) or new top-level type (no token). Kind comes from the body; namespace
          from the FQN; `--path` is PROJECT-relative (the project comes from the FQN too — not
          solution-root-relative). `--in-file` places the type in that file — co-locates into an
          existing file (namespace must match) or names a new one.
        - `cs4ai update <sess> <addr> --token <type_…> --set-body <decl>` — replace a member or a whole
          type in place (a type's name/arity must match the address); cascades if the
          signature changed. Facets: `--set-comment`, `--set-namespace`, `--set-usings`,
          `--set-attributes '[A],[B]'` (whole-replace), `--set-file <path>` (move a TYPE to a file).
        - `cs4ai rename <sess> <addr> <new-name> --token <type_…> [--set-file "New.cs"]` · `cs4ai move
          <sess> <member> <target> --token …` · `cs4ai delete <sess> <addr> --token <type_…>`. rename/move/delete
          rewrite call sites. `--set-file` puts a type in a file (intra-project): a new file (git mv
          when the type was alone in its old file, else extracts it) or co-locates into an existing
          file (namespace must match). The frame says which; folding it into `rename` is the usual fix
          for a renamed type whose file name is now stale.
        - Any multi-line body → write a file and use `--from <file>` (shell-safe — inline multi-line
          `--set-body` risks a quoting hang). Inline `--set-body` is for one-liners; `--stdin` reads stdin.

        Graph / structure (immediate; writes disk + full build + reload):
        - `cs4ai create-project <sess> <name> --path <dir> [--template console]`
        - `cs4ai add-reference <sess> <proj.csproj> <package|ref.csproj> [--version <v>]`
        - also: update-project / delete-project / delete-reference.

        One result format everywhere (no JSON): a status line, then `[op address · path · N lines · token]`
        frames (type source; structure ops echo the affected csproj in full and the solution file as
        a `+`/`-` line delta), then a trailing `[build …]` block.
        Structure output reads exactly like an edit — learn it once. The exit code answers ONLY "was the
        command valid?" (0 = valid; 1 bad-args, 2 inputs-unreadable, 3 ambiguous, 4 not-found, 5 stale,
        6 no-config, 7 no-session). Build/Roslyn errors live in the body, never the exit code.

        Build axis: every command carries a `[build …]` block. Truth source by command class:
        - code edits → Roslyn, **CS* only** (silent on NU*/MSBuild — a code edit can't touch that layer).
        - graph edits + `verify` → a real `dotnet build`, **full** picture (CS*+NU*+MSBuild).
          [build failed · 1 new error · 0 new warnings · 2 preexisting]
          + error CS0246 · src/Bar.cs:5 · type 'Baz' not found        (+ = you introduced it)
            warning CS0168 · src/Foo.cs:9 · 'x' declared but never used  ( = was already there)
          [build failed]
        - rollup: `passed` | `passed_with_warnings` | `failed`. `failed` = you introduced a NEW error.
        - An edit that breaks the build still exits 0. Read the rollup, not the exit code. Fix `+`-tagged
          (new) diagnostics; ` `-tagged ones predate you.

        Finish a session:
        - `cs4ai build <sess>` — fast Roslyn CS re-check. `cs4ai run-test <sess>` — tests on disk.
        - `cs4ai verify <sess>` — the authoritative full build + tests; the final truth (catches NU*/MSBuild a
          code edit can't see). A report, not a gate. Flushes the `--log` transcript if enabled.
          `--raw` appends the verbatim dotnet transcript when the curated [build] block isn't enough.
        - A failed build does NOT undo your edit — fix the code (edits are already on disk). To roll
          back, use git.

        Recovery (the body carries what the retry needs):
        - exit 5 stale/missing `--token` → the response IS the read: current shape + fresh `type_`
          token. Re-cite it.
        - exit 6 needs config → ask the user for a preset (recommend sa1201), `cs4ai init <slnx>
          <preset>`, retry. (A solution created via `session` already has one.)
        - exit 7 no session → lead with a valid `sess_` token (`cs4ai session` mints one).

        Exit codes: 0 ok · 1 usage · 2 file/parse · 3 ambiguous · 4 not found · 5 stale token ·
        6 needs config · 7 no session.
        """;
}
