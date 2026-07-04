using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// One session: a <b>work-span</b>, not a transaction (the 2026-06-29 pivot). It holds no solution
/// fork — edits write through to the host's single live <see cref="Solution"/> immediately, and git
/// owns undo. The session carries only span-scoped state: the routing token, the two diagnostic
/// baselines (seeded at open via a full build), the last computed build outcome, the test baseline,
/// and the optional command transcript.
/// </summary>
internal sealed class EditSession
{
    public required string Token { get; init; }

    public DateTime LastTouchUtc { get; set; } = DateTime.UtcNow;

    /// <summary>"none" | "green" | "red" — legacy test rollup word (Settled #44). The build axis
    /// flows through <see cref="CachedOutcome"/>.</summary>
    public string LastTests { get; set; } = "none";

    /// <summary>The <b>CS-only</b> diagnostic keyset from Roslyn, seeded at session open. Code edits
    /// (Roslyn truth source) tag/delta against this — same vocabulary, exact key match. Re-seeded by
    /// graph edits and <c>verify</c> after a workspace reload.</summary>
    public HashSet<string>? RoslynBaseline { get; set; }

    /// <summary>The <b>full</b> diagnostic keyset (CS\*+NU\*+MSBuild) parsed from a real
    /// <c>dotnet build</c>, seeded at session open. Graph edits and <c>verify</c> (full-build truth
    /// source) tag/delta against this — so an <c>add-reference</c> NU* absent at open reads as
    /// <c>new</c>. Re-seeded after each full build.</summary>
    public HashSet<string>? BuildBaseline { get; set; }

    /// <summary>The build axis: the last computed <see cref="BuildOutcome"/>. Seeded at open and
    /// refreshed by every edit / graph edit / verify; reads carry it without recompiling.</summary>
    public BuildOutcome? CachedOutcome { get; set; }

    /// <summary>Failed-test names at session open, computed lazily on first <c>run-test</c>/<c>verify</c>.</summary>
    public HashSet<string>? TestBaseline { get; set; }

    /// <summary>Set when the session was opened with <c>--log</c>: every command is appended to
    /// <see cref="CommandLog"/> and the whole transcript is flushed to a file on <c>verify</c>.</summary>
    public bool LoggingEnabled { get; set; }

    /// <summary>The in-memory command transcript — one entry per command,
    /// <c>"#N exit code: X\n&lt;cmd&gt;"</c>. Empty unless <see cref="LoggingEnabled"/>.</summary>
    public List<string> CommandLog { get; } = new();
}

/// <summary>
/// The daemon's brain — and, in tests, an in-process stand-in for the daemon. Owns the warm
/// <see cref="Solution"/>, the `.cs4aiconfig` (held under an exclusive-ish write lock per
/// Settled #30), the single active <see cref="EditSession"/>, and the verb dispatch shared by
/// the named-pipe server and the engine's in-process mode. Requests are serialized through one
/// gate; the host's state is effectively single-threaded.
/// </summary>
internal sealed class SolutionHost : IAsyncDisposable
{
    public static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(10);

    public string SlnxPath { get; }
    public string RepoRootPath { get; }
    public string PipeKey { get; }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Workspace? _workspace;
    private Solution? _solution;
    private DateTime _loadedAtUtc;          // when the warm image last converged with disk (load or write-through)
    private Cs4AiConfig? _config;
    private FileStream? _configLock;
    private EditSession? _session;

    public SolutionHost(string slnxPath)
    {
        SlnxPath = Path.GetFullPath(slnxPath);
        RepoRootPath = RepoRoot.From(SlnxPath);
        PipeKey = DaemonProtocol.PipeKeyFor(SlnxPath);
    }

    /// <summary>Verbs routed by the <c>.slnx</c> positional rather than a session token — the
    /// bootstrap and daemon/config verbs. Everything else leads with a <c>sess_</c> token.</summary>
    private static readonly HashSet<string> SolutionRoutedVerbs = new(StringComparer.Ordinal)
    {
        "session", "init", "reload", "stop-daemon",
    };

    /// <summary>Verbs allowed on an empty (zero-project) solution. <c>create-project</c> adds the
    /// first project; the lifecycle/daemon verbs are harmless. Every other (code-touching) verb is
    /// refused until a project exists.</summary>
    private static readonly HashSet<string> EmptyOkVerbs = new(StringComparer.Ordinal)
    {
        "create-project", "verify", "session", "init", "reload", "stop-daemon",
    };

    /// <summary>Verbs that require a <c>.cs4aiconfig</c> (exit 6 first): edits canonicalize, reads
    /// render with config. <c>session</c>/structure aren't gated — they bootstrap before config
    /// exists (a new solution's <c>session</c> writes a default config as part of the bootstrap).</summary>
    private static readonly HashSet<string> ConfigGatedVerbs = new(StringComparer.Ordinal)
    {
        "create", "update", "rename", "delete", "move",
        "discover", "inspect",
        "build", "run-test", "verify",
    };

    public async Task<Cs4AiResult> HandleAsync(
        string[] args, TextReader? stdin = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // The active session before dispatch. For `session` it's null (minted during the call);
            // for `commit`/`discard` it's the session about to end — captured here so the transcript
            // is still reachable (and, for commit, flushable) after `_session` is cleared. Recording
            // wraps every HandleCoreAsync return path (incl. the config/empty-solution refusals),
            // and stays inside the gate so the in-memory buffer writes are serialized.
            var logTarget = _session;
            var startedIndex = await BeginCommandAsync(logTarget, args, ct); // "#N started", flushed now
            var result = await HandleCoreAsync(args, stdin, ct);
            await RecordCommandAsync(args, logTarget ?? _session, result, startedIndex, ct);
            return result;
        }
        catch (Exception e)
        {
            return Cs4AiResult.FileError($"{args.FirstOrDefault()}: {e.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Cs4AiResult> HandleCoreAsync(string[] args, TextReader? stdin, CancellationToken ct)
    {
        if (args.Length == 0) return Cs4AiResult.Usage(Help.Top);
        var verb = args[0];
        var rest = args[1..];

        // Solution-routed verbs carry the .slnx positional (must match this host); strip it.
        // Session-routed verbs lead with a sess_ token, which their handlers consume — leave it.
        if (SolutionRoutedVerbs.Contains(verb))
        {
            var (stripped, namedSlnx) = ArgParse.StripSolutionPositional(rest);
            if (namedSlnx is not null &&
                !string.Equals(Path.GetFullPath(namedSlnx), SlnxPath, StringComparison.OrdinalIgnoreCase))
            {
                return Cs4AiResult.UsageError(
                    $"this call routed to the daemon for '{SlnxPath}' but names '{namedSlnx}'.");
            }
            rest = stripped;
        }

        ExpireIdleSession();

        // Drift reload: if an external edit changed a file under the warm image, drop it so the next
        // load re-reads disk. The token then reflects reality — a stale cited token gets exit 5.
        if (_solution is not null && DiskDriftedSince())
        {
            _solution = null;
            _workspace?.Dispose();
            _workspace = null;
        }

        if (ConfigGatedVerbs.Contains(verb))
        {
            var needsConfig = CheckConfig();
            if (needsConfig is { } nc) return nc;
            _config ??= Cs4AiConfig.TryLoad(RepoRootPath); // load before taking the write lock
            EnsureConfigLock();
        }

        // Empty-solution guard: with no projects, the only meaningful edit is create-project. A
        // workspace that won't LOAD (e.g. a bad reference broke MSBuild) is not "empty" — don't block;
        // let the verb run (a code edit surfaces the real load error; delete-reference recovers).
        if (!EmptyOkVerbs.Contains(verb))
        {
            Solution? committed = null;
            try { committed = await GetCommittedSolutionAsync(ct); }
            catch { /* unloadable ≠ empty — proceed */ }
            if (committed is not null && !committed.Projects.Any())
                return Cs4AiResult.UsageError(
                    "this solution has no projects yet — `create-project <sess> <name> --path <dir>` " +
                    "is the only edit until one exists.");
        }

        return verb switch
        {
            "create" or "update" or "rename" or "delete" or "move"
                               => await DispatchEditAsync(verb, rest, stdin, ct),

            "discover"         => await Reads.RunDiscover(this, rest, ct),
            "inspect"          => await Reads.RunInspect(this, rest, ct),

            "create-project" or "update-project" or "delete-project"
                or "add-reference" or "delete-reference"
                               => await StructureOps.RunAsync(this, verb, rest, ct),

            "session"          => await RunSession(rest, ct),
            "build"            => await RunBuildVerb(rest, ct),
            "run-test"         => await RunTestVerb(rest, ct),
            "verify"           => await RunVerify(rest, ct),

            "init"             => await RunInit(rest, ct),
            "reload"           => RunReload(),

            _ => Cs4AiResult.UsageError($"unknown command '{verb}'. Run 'cs4ai --help'."),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  --log — a LIVE per-session command journal (opt-in on `session`). Each command is written to
    //  `cs4ai_<token>.log` as `#N started` and flushed BEFORE dispatch, then rewritten with its exit
    //  code and re-flushed on completion. So a command that hangs (never completes) still leaves a
    //  visible `#N started` entry — the diagnosability the completion-only log lacked.
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Write the "#N started" marker for a command and flush it to disk. Returns the entry
    /// index (to rewrite on completion), or null if logging isn't enabled.</summary>
    private async Task<int?> BeginCommandAsync(EditSession? session, string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || session is not { LoggingEnabled: true }) return null;
        session.CommandLog.Add($"#{session.CommandLog.Count + 1} started\n{RenderCommandLine(args)}");
        var idx = session.CommandLog.Count - 1;
        await FlushLogAsync(session, ct);
        return idx;
    }

    private async Task RecordCommandAsync(
        string[] args, EditSession? session, Cs4AiResult result, int? startedIndex, CancellationToken ct)
    {
        if (args.Length == 0 || session is not { LoggingEnabled: true }) return;

        int number = (startedIndex ?? session.CommandLog.Count) + 1;
        var entry = $"#{number} exit code: {result.ExitCode}\n{RenderCommandLine(args)}";
        if (startedIndex is { } idx && idx < session.CommandLog.Count)
            session.CommandLog[idx] = entry;      // replace the "started" marker in place
        else
            session.CommandLog.Add(entry);        // `session` verb: no pre-dispatch target existed
        await FlushLogAsync(session, ct);         // live journal — flush every command
    }

    private async Task FlushLogAsync(EditSession session, CancellationToken ct) =>
        await File.WriteAllTextAsync(
            Path.Combine(RepoRootPath, $"cs4ai_{session.Token}.log"),
            string.Join("\n\n", session.CommandLog) + "\n", ct);

    /// <summary>Reconstruct the issued command line for the transcript: <c>cs4ai</c> + the argv,
    /// double-quoting any token with whitespace and dropping the internal <c>--created</c> marker
    /// (it's a routing detail, not something the user typed).</summary>
    internal static string RenderCommandLine(string[] args)
    {
        var parts = args.Where(a => a != "--created").Select(a =>
            a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a);
        return "cs4ai " + string.Join(' ', parts);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Edit dispatch — interpreter (argv → SemanticOperation) → CSEngine.Execute.
    //  The session-token-first positional routes the call; the type token (--token) guards
    //  staleness. One SemanticOperation → one TypeOperations group → all-or-nothing Execute.
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<Cs4AiResult> DispatchEditAsync(
        string verb, string[] rest, TextReader? stdin, CancellationToken ct)
    {
        var interp = EditInterpreters.For(verb);
        if (interp is null) return Cs4AiResult.UsageError($"unknown edit verb '{verb}'.");

        var (op, parseErr) = await interp.ParseAsync(rest, stdin, ct);
        if (parseErr is { } pe) return pe;

        var session = ValidateSession(op!.SessionToken);
        if (session is null)
        {
            // The first positional was the address target for the cold-start refusal (shape + token).
            var target = op.Op.Source ?? op.Op.Destination;
            return await NoSessionRefusalAsync(op.SessionToken, target, ct);
        }

        var engine = new CSEngine(this, session, Config);
        var group = new TypeOperations { Token = op.TypeToken, FileHeaderToken = op.FileHeaderToken, Ops = [op.Op] };
        return await engine.ExecuteAsync([group], ct);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Warm solution + config
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The live in-memory <see cref="Solution"/> (== disk after write-through), or null
    /// before first load. The engine reads it synchronously mid-edit; call
    /// <see cref="GetCommittedSolutionAsync"/> to force a load.</summary>
    public Solution? CurrentSolution => _solution;

    public async Task<Solution> GetCommittedSolutionAsync(CancellationToken ct)
    {
        if (_solution is not null) return _solution;

        // A zero-project solution has nothing for Roslyn to load (and MSBuildWorkspace stalls on an
        // empty solution file). Represent it as an empty in-memory Solution; the first create-project
        // writes a real project to disk and the reload then does the genuine MSBuild load.
        if (!SolutionFileHasProjects(SlnxPath))
        {
            _workspace?.Dispose();
            var adhoc = new AdhocWorkspace();
            _workspace = adhoc;
            _solution = adhoc.CurrentSolution;
            _loadedAtUtc = DateTime.UtcNow;
            return _solution;
        }

        var (workspace, solution, error) = await WorkspaceLoader.LoadWithWorkspaceAsync(SlnxPath, ct);
        if (error is { } e) throw new InvalidOperationException(e.Error ?? "workspace load failed");

        _workspace?.Dispose();
        _workspace = workspace;
        _solution = solution;
        _loadedAtUtc = DateTime.UtcNow;
        return _solution!;
    }

    /// <summary>Cheap check for whether a solution file references any project — without invoking
    /// MSBuild. A freshly-created (empty) solution has none, so we skip the warm load entirely.</summary>
    private static bool SolutionFileHasProjects(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                ? text.Contains("<Project", StringComparison.Ordinal)
                : text.Contains("Project(", StringComparison.Ordinal); // classic .sln
        }
        catch
        {
            return true; // unsure → attempt the real load rather than wrongly treat as empty
        }
    }

    /// <summary>After a structural op has written disk, drop the warm workspace and re-load. A clean
    /// active session (no staged edits — structure ops refuse otherwise) is re-based onto the new
    /// solution so a subsequent staged edit sees the structural change (e.g. the new project).</summary>
    /// <summary>After a graph edit has written disk: rebuild the in-memory <see cref="Solution"/> from
    /// the changed csproj (the load-bearing seam — a flag flip would leave the next code edit reporting
    /// a phantom CS0246 on a freshly-referenced type), run a full build, and report it **against the
    /// prior `BuildBaseline`** so a diagnostic the graph edit introduced (e.g. add-reference → NU1510)
    /// reads as <c>new</c>. Then re-seed both baselines to the post-edit floor for later commands.</summary>
    public async Task<BuildOutcome> ReloadAndRefreshSessionAsync(CancellationToken ct)
    {
        _solution = null;
        _workspace?.Dispose();
        _workspace = null;

        if (_session is null)
        {
            try { await GetCommittedSolutionAsync(ct); } catch { /* tolerate */ }
            return BuildOutcomes.Empty;
        }

        // Full build FIRST — it captures NU*/MSBuild diagnostics (e.g. NU1510) even when the resulting
        // project state won't load into Roslyn's MSBuildWorkspace. Report them vs the prior baseline so
        // the graph edit that introduced them reads as `new`.
        var priorBuildBaseline = (IReadOnlySet<string>?)_session.BuildBaseline ?? new HashSet<string>();
        var (_, diags, _) = await BuildAndTest.BuildOnlyAsync(SlnxPath, ct);
        var reported = BuildOutcomes.FromBuild(diags, priorBuildBaseline, Relativize);
        _session.BuildBaseline = BuildOutcomes.BuildBaselineKeys(diags, Relativize);
        _session.TestBaseline = null;
        _session.CachedOutcome = reported;

        // Rebuild the Roslyn workspace from disk (the seam). TOLERATE a load failure: a graph edit that
        // broke the load (e.g. NU1510 escalated to a fatal MSBuild error on net10) still REPORTS its
        // buildOutcome above instead of aborting with exit 2 — and `delete-reference` reloads cleanly
        // once the offending reference is gone.
        try
        {
            var fresh = await GetCommittedSolutionAsync(ct);
            _session.RoslynBaseline = await BuildOutcomes.BaselineKeysAsync(fresh, Relativize, ct);
        }
        catch { /* unloadable in this state; code edits surface it, delete-reference recovers */ }

        return reported;
    }

    public Cs4AiConfig Config => _config ??= Cs4AiConfig.TryLoad(RepoRootPath)
        ?? throw new InvalidOperationException("no .cs4aiconfig (config gate should have fired)");

    private Cs4AiResult? CheckConfig()
    {
        if (File.Exists(Cs4AiConfig.PathFor(RepoRootPath))) return null;
        var payload = NeedsConfigPayload.For(RepoRootPath, SlnxPath);
        return Cs4AiResult.NeedsConfig(payload.ToText());
    }

    /// <summary>The daemon holds a write lock on `.cs4aiconfig` for its lifetime (Settled #30):
    /// readers proceed, hand-edits get "file in use" — the intended hand-off.</summary>
    private void EnsureConfigLock()
    {
        if (_configLock is not null) return;
        var path = Cs4AiConfig.PathFor(RepoRootPath);
        if (!File.Exists(path)) return;
        try
        {
            _configLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException)
        {
            // Another holder (a second host in tests). Proceed without the lock; the gate that
            // matters in production is the single daemon per solution.
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Sessions
    // ─────────────────────────────────────────────────────────────────────────────

    public EditSession? ActiveSession => _session;

    // The fate of the most recently ended session. An agent has no clock, so "10-min idle
    // expiry" is unusable knowledge — instead, citing a dead token gets told exactly what
    // happened to it ("session expired, staged edits lost") at the moment it matters.
    private string? _endedSessionToken;
    private string? _endedSessionFate;

    private void ExpireIdleSession()
    {
        if (_session is not null && DateTime.UtcNow - _session.LastTouchUtc > SessionIdleTimeout)
        {
            // Abandoned sessions evaporate — nothing reached disk (Settled #40).
            EndSession("expired (idle) — staged edits were lost; create a new session and re-stage");
        }
    }

    private void EndSession(string fate)
    {
        if (_session is null) return;
        _endedSessionToken = _session.Token;
        _endedSessionFate = fate;
        _session = null;
    }

    /// <summary>"session <token> expired …" when the cited token belongs to the most recently
    /// ended session; null otherwise.</summary>
    private string? FateOf(string? citedToken) =>
        citedToken is not null && string.Equals(citedToken, _endedSessionToken, StringComparison.Ordinal)
            ? $"session {citedToken} {_endedSessionFate}"
            : null;

    /// <summary>Validate a cited session token. Returns the session (touched) or null.</summary>
    public EditSession? ValidateSession(string? cited)
    {
        ExpireIdleSession();
        if (cited is null || _session is null) return null;
        if (!string.Equals(cited, _session.Token, StringComparison.Ordinal)) return null;
        _session.LastTouchUtc = DateTime.UtcNow;
        return _session;
    }

    private async Task<EditSession> MintSessionAsync(CancellationToken ct)
    {
        var live = await GetCommittedSolutionAsync(ct);
        var random = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var token = DaemonProtocol.NewSessionToken(PipeKey, random);
        _session = new EditSession { Token = token };

        // Seed both baselines at open via a full build, so the agent knows exactly where it stands
        // and a later add-reference NU* (absent now) reads as `new`. One build, amortized over the span.
        await SeedBaselinesAsync(live, ct);
        return _session;
    }

    /// <summary>Seed (or re-seed) the two diagnostic baselines + the cached outcome from a full build:
    /// <see cref="EditSession.RoslynBaseline"/> (CS\* for code-edit deltas) and
    /// <see cref="EditSession.BuildBaseline"/> (full CS\*+NU\*+MSBuild for graph/verify deltas). The
    /// returned outcome is the full picture with everything tagged <c>preexisting</c> (the floor).</summary>
    private async Task<BuildOutcome> SeedBaselinesAsync(Solution live, CancellationToken ct)
    {
        _session!.RoslynBaseline = await BuildOutcomes.BaselineKeysAsync(live, Relativize, ct);
        // Build + test at open so the test baseline means "failing at open" — the floor for the
        // new-vs-preexisting attribution. Capturing it lazily on the first run-test/verify (as it was)
        // absorbed a test the agent added mid-span, hiding a red suite behind an empty delta (bug #4).
        var run = await BuildAndTest.RunAsync(SlnxPath, ct);
        _session.BuildBaseline = BuildOutcomes.BuildBaselineKeys(run.Diagnostics, Relativize);
        _session.TestBaseline = run.BuildPassed
            ? new HashSet<string>(run.TestFailures, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal); // no build → nothing to diff against
        var outcome = BuildOutcomes.FromBuild(run.Diagnostics, _session.BuildBaseline, Relativize);
        _session.CachedOutcome = outcome;
        return outcome;
    }

    /// <summary>
    /// The exit-7 refusal. When no session is active, mints one and hands it back along with the
    /// target's current shape + staleness token, so the coldest start is a two-step (the design
    /// doc's "The token is always required; the refusal hands you one" subsection). When a
    /// session IS active, no new session can be minted (one-at-a-time, Settled #42) — the
    /// refusal names the active session instead. Under AI-only writes the active session is
    /// almost certainly the caller's own, so the token is included for self-healing.
    /// </summary>
    public async Task<Cs4AiResult> NoSessionRefusalAsync(
        string? citedToken, string? targetAddress, CancellationToken ct)
    {
        ExpireIdleSession();
        if (_session is not null)
        {
            return Cs4AiResult.NoSession(string.Join("\n",
                "no-session: a session is already active for this solution (one session at a time)",
                $"active-session: {_session.Token}",
                "recovery: lead with the active token above — it is almost certainly yours") + "\n");
        }

        var priorSessionFate = FateOf(citedToken);
        var session = await MintSessionAsync(ct);
        var live = await GetCommittedSolutionAsync(ct);

        INamedTypeSymbol? targetType = null;
        string? targetToken = null;
        if (targetAddress is not null)
        {
            try
            {
                var (resolved, _) = await AddressResolver.ResolveAsync(live, targetAddress, ct);
                targetType = resolved.Symbol as INamedTypeSymbol ?? resolved.Symbol?.ContainingType;
                if (targetType is not null) targetToken = TokenBuilder.TokenString(targetType);
            }
            catch { /* the shape is a convenience, not a contract — refusal still hands the session */ }
        }

        // Plain framed text (no JSON). The recovery line spells out the COMPLETE re-fire — staleness
        // token included — and the target's current shape rides as an inspect frame, so the agent
        // re-cites in one step (the refusal IS the read).
        var lines = new List<string> { "no-session" };
        if (priorSessionFate is not null) lines.Add($"prior-session: {priorSessionFate}");
        lines.Add($"session: {session.Token}");
        lines.Add(targetToken is null
            ? $"recovery: re-fire the same command citing --session {session.Token}"
            : $"recovery: re-fire the same command citing --session {session.Token} --token {CSEngine.TypeTokenPrefix}{targetToken}");
        if (targetType is not null)
            lines.AddRange(FrameRenderer.FullType(targetType, Relativize, "inspect"));
        return Cs4AiResult.NoSession(string.Join("\n", lines) + "\n");
    }

    /// <summary>The view a read sees. There is one live view now (== disk); the session token gates
    /// access (config + routing) but never selects a fork.</summary>
    public async Task<(Solution? view, Cs4AiResult? error)> ResolveViewAsync(
        string? sessionToken, CancellationToken ct)
    {
        if (sessionToken is null)
            return (await GetCommittedSolutionAsync(ct), null);

        var session = ValidateSession(sessionToken);
        if (session is null)
        {
            var reason = FateOf(sessionToken) ?? (_session is null
                ? "no session is active"
                : "the cited token does not match the active session");
            var lines = new List<string> { $"no-session: {reason}" };
            if (_session is not null) lines.Add($"active-session: {_session.Token}");
            lines.Add(_session is not null
                ? "recovery: lead with the active token above"
                : "recovery: run `cs4ai session <solution>` for a fresh token, then re-fire this command with it");
            return (null, Cs4AiResult.NoSession(string.Join("\n", lines) + "\n"));
        }
        return (await GetCommittedSolutionAsync(ct), null);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  session — open a work-span
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Open a <b>work-span</b>: mint the routing token, seed both diagnostic baselines via a full
    /// build, and return the <c>solution: opened|created</c> field plus the **opening
    /// <see cref="BuildOutcome"/>** so the agent knows exactly where it stands from command one. Edits
    /// write through immediately; git owns undo — there is nothing to commit or discard.
    /// </summary>
    private async Task<Cs4AiResult> RunSession(string[] rest, CancellationToken ct)
    {
        ExpireIdleSession();
        bool created = Array.IndexOf(rest, "--created") >= 0;

        if (_session is not null)
        {
            // The token goes LAST with no trailing punctuation: a period flush against it gets
            // swept up by loose extraction (a live agent harvested `sess_….` and every call bounced).
            return Cs4AiResult.UsageError(
                "a session is already active — one session at a time; lead with it on every call: " +
                _session.Token);
        }

        var session = await MintSessionAsync(ct); // runs the full opening build → CachedOutcome
        session.LoggingEnabled = Array.IndexOf(rest, "--log") >= 0;
        var status = new List<string> { $"session {(created ? "created" : "opened")} · {SlnxPath} · {session.Token}" };
        if (session.LoggingEnabled) status.Add($"log: cs4ai_{session.Token}.log");
        // The opening buildOutcome is the floor — where you stand before touching anything.
        return Cs4AiResult.Ok(StatusBody(status, session.CachedOutcome));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  build — the fast Roslyn CS re-check of the live view, tagged against RoslynBaseline. (For the
    //  full picture incl. NU*/MSBuild, run `verify`.)
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<Cs4AiResult> RunBuildVerb(string[] args, CancellationToken ct)
    {
        var p = ArgParse.Parse(args);
        var token = p.Positionals.Count > 0 ? p.Positionals[0] : null;
        var session = ValidateSession(token);
        if (session is null) return await NoSessionRefusalAsync(token, null, ct);

        var live = await GetCommittedSolutionAsync(ct);
        session.RoslynBaseline ??= await BuildOutcomes.BaselineKeysAsync(live, Relativize, ct);
        var outcome = await BuildOutcomes.ComputeAsync(live, session.RoslynBaseline, Relativize, ct);
        session.CachedOutcome = outcome;

        return Cs4AiResult.Ok(StatusBody(["build"], outcome));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  run-test — tests-only fast path. Edits write through, so this runs `dotnet test` on disk
    //  directly (no shadow copy). Reports new failures vs the session-open baseline + the full
    //  buildOutcome from the build it did.
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<Cs4AiResult> RunTestVerb(string[] args, CancellationToken ct)
    {
        var p = ArgParse.Parse(args);
        var token = p.Positionals.Count > 0 ? p.Positionals[0] : null;
        var session = ValidateSession(token);
        if (session is null) return await NoSessionRefusalAsync(token, null, ct);

        // Test baseline: the failures present at session open. Lazily computed (first run-test/verify).
        session.TestBaseline ??= await ComputeTestBaselineAsync(ct);

        var run = await BuildAndTest.RunAsync(SlnxPath, ct);
        var buildBaseline = (IReadOnlySet<string>?)session.BuildBaseline ?? new HashSet<string>();
        var outcome = BuildOutcomes.FromBuild(run.Diagnostics, buildBaseline, Relativize);
        session.CachedOutcome = outcome;

        var (verdict, last) = TestVerdictLines(run, session.TestBaseline);
        session.LastTests = last;
        var status = new List<string> { "run-test" };
        status.AddRange(verdict);
        return Cs4AiResult.Ok(StatusBody(status, outcome));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  verify — the end-of-work checkpoint. Mandatory full build + tests; the AUTHORITATIVE truth.
    //  A report, not a gate (edits are already on disk; nothing to refuse). Re-seeds the baselines.
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<Cs4AiResult> RunVerify(string[] args, CancellationToken ct)
    {
        var p = ArgParse.Parse(args);
        var token = p.Positionals.Count > 0 ? p.Positionals[0] : null;
        var session = ValidateSession(token);
        if (session is null) return await NoSessionRefusalAsync(token, null, ct);

        session.TestBaseline ??= await ComputeTestBaselineAsync(ct);

        var run = await BuildAndTest.RunAsync(SlnxPath, ct);
        var buildBaseline = (IReadOnlySet<string>?)session.BuildBaseline ?? new HashSet<string>();
        var outcome = BuildOutcomes.FromBuild(run.Diagnostics, buildBaseline, Relativize);
        session.CachedOutcome = outcome;

        var (verdict, last) = TestVerdictLines(run, session.TestBaseline);
        session.LastTests = last;

        // Re-seed the baselines to this authoritative floor for any further work in the span.
        session.RoslynBaseline = await BuildOutcomes.BaselineKeysAsync(
            await GetCommittedSolutionAsync(ct), Relativize, ct);
        session.BuildBaseline = BuildOutcomes.BuildBaselineKeys(run.Diagnostics, Relativize);
        if (run.BuildPassed)
            session.TestBaseline = new HashSet<string>(run.TestFailures, StringComparer.Ordinal);

        var status = new List<string> { "verify" };
        status.AddRange(verdict);
        var body = StatusBody(status, outcome);

        // --raw: the verbatim dotnet transcript, on demand. The [build] block stays the curated,
        // counted, delta'd truth; this is the authentic MSBuild prose for when the agent wants the
        // full story (restore chatter, target timings, everything) — clearly fenced, never parsed.
        if (p.Raw && run.Raw.Length > 0)
            body += string.Join("\n", FrameRenderer.Group(
                "raw dotnet output", run.Raw.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))) + "\n";

        return Cs4AiResult.Edited(body);
    }

    /// <summary>The test verdict, driven off the <b>absolute</b> failing set (not a delta) so
    /// <c>tests: passed</c> means zero failing tests, period — a red suite can never read green
    /// (bug #4). The session-open baseline is used only to <i>annotate</i> new vs preexisting.
    /// Returns the status line(s) and the <see cref="EditSession.LastTests"/> value.</summary>
    internal static (List<string> lines, string lastTests) TestVerdictLines(
        BuildAndTest.Result run, IReadOnlySet<string> baseline)
    {
        if (!run.BuildPassed)
            return (["tests: skipped (build failed)"], "none");

        var failing = run.TestFailures;
        var failCount = Math.Max(run.FailedCount, failing.Count);

        if (failCount == 0)
        {
            // TestsPassed guards a nonzero exit with no parsed failures (a crashed/aborted run).
            if (!run.TestsPassed)
                return (["tests: failed (the test run exited nonzero with no parsed failures — see verify output)"], "red");
            if (run.TotalTests == 0)
                return (["tests: no tests ran"], "none");
            return ([$"tests: passed · {run.TotalTests} run"], "green");
        }

        var newOnes = failing.Where(f => !baseline.Contains(f)).ToList();
        var lines = new List<string>
        {
            $"tests: failed · {failCount} failing ({newOnes.Count} new, {failing.Count - newOnes.Count} preexisting)",
        };
        lines.AddRange(failing.Select(f => $"failed-test: {f}{(baseline.Contains(f) ? " (preexisting)" : "")}"));
        return (lines, "red");
    }

    /// <summary>A framed-text result body: the status line(s) then the <c>[build …]</c> block (when
    /// there's an outcome) — the same shape edits emit, so every command reads alike. No JSON.</summary>
    private static string StatusBody(IReadOnlyList<string> statusLines, BuildOutcome? outcome)
    {
        var lines = new List<string>(statusLines);
        if (outcome is not null) lines.AddRange(FrameRenderer.BuildBlock(outcome));
        return string.Join("\n", lines) + "\n";
    }

    private async Task<HashSet<string>> ComputeTestBaselineAsync(CancellationToken ct)
    {
        var baseline = await BuildAndTest.RunAsync(SlnxPath, ct);
        return baseline.BuildPassed
            ? new HashSet<string>(baseline.TestFailures, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal); // doesn't build: nothing to diff against
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Write-through — the pivot's landing point. Adopt a freshly-edited Solution as the live view
    //  and flush its document diff to disk. No commit, no staged fork, no base-snapshot check (the
    //  per-command drift-reload guards external mutation up front). The engine calls this only once
    //  every op in the command has landed in memory, so it's atomic per command; git owns undo.
    // ─────────────────────────────────────────────────────────────────────────────

    public async Task AdoptSolutionAsync(Solution next, CancellationToken ct)
    {
        var prev = _solution;
        if (prev is not null && !ReferenceEquals(next, prev))
        {
            foreach (var pc in next.GetChanges(prev).GetProjectChanges())
            {
                foreach (var docId in pc.GetChangedDocuments().Concat(pc.GetAddedDocuments()))
                {
                    var doc = next.GetDocument(docId);
                    if (doc?.FilePath is null) continue;
                    await WriteFileAsync(doc.FilePath, await doc.GetTextAsync(ct), ct);
                }
                // A whole-type delete that emptied its file removes the document → delete the file.
                foreach (var docId in pc.GetRemovedDocuments())
                {
                    var path = prev.GetDocument(docId)?.FilePath;
                    if (path is null) continue;
                    try { if (File.Exists(path)) File.Delete(path); } catch { /* git is the net */ }
                }
            }
        }
        _solution = next; // the live in-memory image and disk re-converge
        _loadedAtUtc = DateTime.UtcNow; // our own writes are not drift
    }

    /// <summary>Has any source file changed on disk since the warm image last converged with it (an
    /// external IDE/git edit)? Cheap mtime check. A false positive only costs a reload; the token model
    /// then surfaces the real staleness (the cited token won't match the reloaded type).</summary>
    private bool DiskDriftedSince()
    {
        if (_solution is null) return false;
        foreach (var doc in _solution.Projects.SelectMany(p => p.Documents))
        {
            var path = doc.FilePath;
            if (path is null) continue;
            try { if (File.GetLastWriteTimeUtc(path) > _loadedAtUtc) return true; }
            catch { /* unreadable → let the reload/resolve surface it */ }
        }
        return false;
    }

    private static async Task WriteFileAsync(string path, SourceText text, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text.ToString(), text.Encoding ?? new UTF8Encoding(false), ct);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  init / reload
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<Cs4AiResult> RunInit(string[] args, CancellationToken ct)
    {
        var p = ArgParse.Parse(args);
        if (p.Positionals.Count < 1)
            return Cs4AiResult.UsageError("init: usage: cs4ai init <slnx> <preset>");
        var presetName = p.Positionals[0];

        Cs4AiConfig newConfig;
        try { newConfig = Cs4AiConfig.Preset(presetName); }
        catch (ArgumentException ex) { return Cs4AiResult.UsageError($"init: {ex.Message}"); }

        var configPath = Cs4AiConfig.PathFor(RepoRootPath);
        if (File.Exists(configPath))
        {
            Cs4AiConfig? existing = null;
            try { existing = Cs4AiConfig.TryLoad(RepoRootPath); } catch { /* malformed */ }

            if (existing is not null && ConfigEquals(existing, newConfig))
                return Cs4AiResult.Ok(
                    $".cs4aiconfig already set to '{presetName}' — no change.\n");

            // Different preset (or unreadable): exit 1, retry guidance — the race-loser doesn't
            // fail, it retries the original work (Settled #31).
            return Cs4AiResult.UsageError(
                $".cs4aiconfig already exists with different content. The repo's policy reflects " +
                "whichever init landed first — retry your original command. (To genuinely change " +
                $"presets: cs4ai stop-daemon {SlnxPath}, remove .cs4aiconfig, re-run init.)");
        }

        // Fresh write — release the lock around the write, re-acquire after (Settled #30's
        // atomic file + in-memory update, serialized by the host gate).
        _configLock?.Dispose();
        _configLock = null;
        await File.WriteAllTextAsync(configPath, newConfig.ToJson() + "\n", ct);
        _config = newConfig;
        EnsureConfigLock();

        return Cs4AiResult.Ok($"Wrote .cs4aiconfig (preset: {presetName}) to {configPath}\n");
    }

    private static bool ConfigEquals(Cs4AiConfig a, Cs4AiConfig b) =>
        a.Canonicalize == b.Canonicalize
        && a.SortWithinGroup == b.SortWithinGroup
        && a.MemberOrder.SequenceEqual(b.MemberOrder)
        && a.AccessOrder.SequenceEqual(b.AccessOrder);

    private Cs4AiResult RunReload()
    {
        ExpireIdleSession();
        // Safe anytime now: edits write through, so disk is the truth a reload re-reads. Any active
        // work-span continues; only the warm in-memory workspace is dropped.
        _solution = null;
        _workspace?.Dispose();
        _workspace = null;
        return Cs4AiResult.Ok(
            $"reloaded: the warm workspace for '{SlnxPath}' is invalidated; the next call " +
            "re-parses from disk.\n");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────────

    public string Relativize(string? absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return "";
        try { return Path.GetRelativePath(RepoRootPath, absolutePath).Replace('\\', '/'); }
        catch { return absolutePath; }
    }

    public ValueTask DisposeAsync()
    {
        _configLock?.Dispose();
        _workspace?.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
