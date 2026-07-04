using System.Reflection;
using System.Text;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The cs4ai engine: the CLI front. Meta paths run locally; every solution-bearing verb routes
/// to the per-solution daemon (the transparent auto-daemon, the design doc's "Daemon vs.
/// One-Shot" section). Routing key: the <c>--session</c> token's prefix when present — "the
/// token carries the solution" (Settled #45) — otherwise the `.slnx` / `.csproj` positional.
/// <para>
/// <b>In-process mode</b> (<c>new Cs4AiEngine(inProcess: true)</c>) dispatches to a
/// <see cref="SolutionHost"/> registry inside this process instead of a daemon — the library-first
/// path used by tests and by C# callers embedding the engine. Identical verb behavior; only the
/// transport differs.
/// </para>
/// </summary>
public sealed class Cs4AiEngine : IAsyncDisposable
{
    private readonly bool _inProcess;
    private readonly Dictionary<string, SolutionHost> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public Cs4AiEngine() : this(inProcess: false) { }

    public Cs4AiEngine(bool inProcess) => _inProcess = inProcess;

    /// <summary>The tool version, surfaced by <c>cs4ai --version</c>.</summary>
    public static string Version
    {
        get
        {
            var info = typeof(Cs4AiEngine).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(info)) return "0.1.0";
            int plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
    }

    private static readonly HashSet<string> SolutionVerbs = new(StringComparer.Ordinal)
    {
        "create", "update", "rename", "delete", "move",
        "create-project", "update-project", "delete-project", "add-reference", "delete-reference",
        "discover", "inspect",
        "session", "build", "run-test", "verify",
        "init", "reload", "stop-daemon",
    };

    /// <summary>Verbs routed by the <c>.slnx</c> positional (bootstrap + daemon/config). Everything
    /// else leads with a <c>sess_</c> token that carries the solution.</summary>
    private static readonly HashSet<string> SolutionRoutedVerbs = new(StringComparer.Ordinal)
    {
        "session", "init", "reload", "stop-daemon",
    };

    /// <summary>
    /// Parse and dispatch. Never throws for expected error conditions — failures return a
    /// non-zero <see cref="Cs4AiResult.ExitCode"/> with a human-readable error.
    /// </summary>
    public async Task<Cs4AiResult> ExecuteAsync(string[] args, TextReader? stdin = null)
    {
        if (args.Length == 0 || IsHelpFlag(args[0]))
            return Cs4AiResult.Usage(Help.Top);

        // --debug: opt-in daemon trace log (cs4ai-daemon.log next to the solution). A global flag —
        // stripped here so no interpreter ever sees it; it only matters to a daemon this command
        // may spawn (an already-running daemon keeps whatever mode it started with).
        bool debug = Array.IndexOf(args, "--debug") >= 0;
        if (debug) args = Array.FindAll(args, a => a != "--debug");

        var verb = args[0];

        // Meta paths — always local, never routed.
        switch (verb)
        {
            case "--version" or "version":
                return Cs4AiResult.Ok($"cs4ai {Version}\n");
            case "--show-readme":
                return Cs4AiResult.Ok(EmbeddedReadme());
            case "--create-skill":
                return CreateSkill(args.Length > 1 ? args[1] : null);
            case "--daemon":
                // Hidden entry: run as the per-solution daemon. Spawned by DaemonClient with
                // redirected-then-closed stdio — DaemonLog takes over Console first thing, so
                // nothing ever writes to the dead pipes (--debug → cs4ai-daemon.log, else null).
                if (args.Length < 2)
                    return Cs4AiResult.UsageError("--daemon requires the solution path");
                using (var daemonLog = DaemonLog.Create(args[1], debug))
                {
                    daemonLog.Log($"daemon starting · cs4ai {Version} · {Path.GetFullPath(args[1])}");
                    await new DaemonServer(args[1], log: daemonLog).RunAsync();
                    daemonLog.Log("daemon exited.");
                }
                return Cs4AiResult.Ok();
        }

        if (!SolutionVerbs.Contains(verb))
            return Cs4AiResult.UsageError($"unknown command '{verb}'. Run 'cs4ai --help'.");

        // Per-verb help is local — don't spawn a daemon to print usage.
        if (args.Skip(1).Any(a => a is "-h" or "--help"))
            return Cs4AiResult.Usage(Help.For(verb));

        var rest = args[1..];
        // Capture stdin once; it crosses the daemon pipe as text. stdin is OPT-IN — read it only when
        // the caller passed --stdin, so a redirected-but-EOF-less stream never blocks the process.
        string? stdinText = stdin is not null && Array.IndexOf(args, "--stdin") >= 0
            ? await stdin.ReadToEndAsync()
            : null;

        // ── Solution-routed verbs (session/init/reload/stop-daemon): named by the .slnx path. ──
        if (SolutionRoutedVerbs.Contains(verb))
        {
            if (verb == "session")
                return await RouteSessionAsync(rest, stdinText, debug);

            var (_, slnx) = ArgParse.StripSolutionPositional(rest);
            if (slnx is null)
                return Cs4AiResult.UsageError($"{verb}: usage: cs4ai {verb} <slnx-or-csproj> …");
            if (!File.Exists(slnx))
                return Cs4AiResult.FileError($"file not found: {slnx}");

            if (_inProcess) return await ExecuteInProcessAsync(verb, rest, slnx, session: null, stdinText);
            if (verb == "stop-daemon") return await DaemonClient.StopDaemonAsync(slnx, args);
            return await DaemonClient.RouteBySolutionAsync(slnx, args, stdinText, debug);
        }

        // ── Session-routed verbs: lead with the sess_ token (it carries the solution). ──
        var sessToken = ArgParse.Parse(rest).Positionals.FirstOrDefault();
        if (sessToken is null ||
            !sessToken.StartsWith(DaemonProtocol.SessionTokenPrefix, StringComparison.Ordinal))
            return Cs4AiResult.UsageError(
                $"{verb}: lead with the session token (sess_…) — `cs4ai {verb} <sess-token> …`. " +
                "Get one from `cs4ai session <solution>`.");

        if (_inProcess) return await ExecuteInProcessAsync(verb, rest, slnx: null, sessToken, stdinText);
        return await DaemonClient.RouteBySessionAsync(sessToken, args, stdinText);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  session — the bootstrap: resolve (open-or-create) the solution, then mint a token.
    //  cs4ai creates a solution only when handed a name it doesn't already find (version2.md):
    //  the .slnx + dirs + a default .cs4aiconfig are a bootstrap disk write (not source).
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<Cs4AiResult> RouteSessionAsync(string[] rest, string? stdinText, bool debug)
    {
        var p = ArgParse.Parse(rest);
        var pathArg = p.Positionals.Count > 0 ? p.Positionals[0] : null; // default: current directory

        // --format applies only when this session CREATES a solution (no-op on open, so the
        // command stays idempotent). Default slnx; an explicit extension in the path wins.
        var format = p.Format?.ToLowerInvariant();
        if (format is not (null or "sln" or "slnx"))
            return Cs4AiResult.UsageError($"session: --format takes 'slnx' (default) or 'sln', not '{p.Format}'.");

        var (slnx, created, error) = await ResolveSessionSolutionAsync(pathArg, format);
        if (error is { } e) return e;
        if (slnx is null) return Cs4AiResult.FileError("session: could not open or create a solution.");

        // Host args: name the resolved .slnx; inject --created so the host's envelope reports it.
        // --log rides through so the host can enable the session's command transcript.
        var hostArgsList = new List<string> { "session", slnx };
        if (created) hostArgsList.Add("--created");
        if (p.Log) hostArgsList.Add("--log");
        var hostArgs = hostArgsList.ToArray();

        if (_inProcess)
            return await ExecuteInProcessAsync("session", hostArgs[1..], slnx, session: null, stdinText);
        return await DaemonClient.RouteBySolutionAsync(slnx, hostArgs, stdinText, debug);
    }

    /// <summary>
    /// Resolve <c>session</c>'s target from a single path (default: the current directory) — no
    /// name/path ambiguity, no <c>--path</c>. Cases by filesystem reality: an existing solution file
    /// → open; a directory (or cwd) → open its one solution, or create <c>&lt;dir&gt;.slnx</c> when
    /// there's none (ambiguous → exit 3 listing them); an absent solution-file path → create exactly
    /// that file (its extension IS the format — a contradicting <c>--format</c> is exit 1); an
    /// absent directory path → create the dir + a solution named after it. Creation is offloaded to
    /// <c>dotnet new sln</c> (default format slnx, <c>--format sln</c> for classic) and seeds a
    /// default <c>.cs4aiconfig</c>. Returns the full path, whether it was created, or an error.
    /// </summary>
    private static async Task<(string? slnx, bool created, Cs4AiResult? error)> ResolveSessionSolutionAsync(
        string? pathArg, string? format)
    {
        var path = pathArg is null ? Directory.GetCurrentDirectory() : Path.GetFullPath(pathArg);

        if (File.Exists(path) && ArgParse.LooksLikeSolution(path))
            return (path, false, null);

        if (Directory.Exists(path))
            return await ResolveInDirectoryAsync(path, format);

        // Non-existent: a solution-file name → create that file; otherwise a directory to create.
        if (ArgParse.LooksLikeSolution(path))
        {
            // The explicit extension IS the format. A contradicting --format rejects — the grammar
            // rule: a contradiction is never silently dropped.
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            var extFormat = ext is "sln" or "slnx" ? ext : null;
            if (extFormat is not null && format is not null && format != extFormat)
                return (null, false, Cs4AiResult.UsageError(
                    $"session: the path says '.{extFormat}' but --format says '{format}' — drop one."));

            var fileDir = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(fileDir);
            return await CreateSolutionAsync(
                fileDir, Path.GetFileNameWithoutExtension(path), extFormat ?? format ?? "slnx",
                formatExplicit: extFormat is not null || format is not null);
        }
        Directory.CreateDirectory(path);
        return await ResolveInDirectoryAsync(path, format);
    }

    private static async Task<(string?, bool, Cs4AiResult?)> ResolveInDirectoryAsync(string dir, string? format)
    {
        var solutions = Directory.EnumerateFiles(dir, "*.slnx")
            .Concat(Directory.EnumerateFiles(dir, "*.sln"))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        if (solutions.Count == 1) return (solutions[0], false, null);
        if (solutions.Count > 1)
            return (null, false, Cs4AiResult.Ambiguous(
                $"session: {solutions.Count} solutions in '{dir}' — name one:\n" +
                string.Join("\n", solutions.Select(s => "  " + s))));

        return await CreateSolutionAsync(
            dir, new DirectoryInfo(dir).Name, format ?? "slnx", formatExplicit: format is not null);
    }

    private static async Task<(string?, bool, Cs4AiResult?)> CreateSolutionAsync(
        string dir, string name, string format, bool formatExplicit)
    {
        // Only slnx needs the flag (`--format sln` would fail on SDKs that predate the option).
        var newArgs = format == "slnx"
            ? new[] { "new", "sln", "-n", name, "-o", dir, "--format", "slnx" }
            : new[] { "new", "sln", "-n", name, "-o", dir };
        var (ok, log) = await DotnetCli.RunAsync(dir, default, newArgs);

        string? PathIf(string ext) => File.Exists(Path.Combine(dir, name + ext)) ? Path.Combine(dir, name + ext) : null;
        var created = format == "slnx" ? PathIf(".slnx") ?? PathIf(".sln") : PathIf(".sln") ?? PathIf(".slnx");

        // A DEFAULTED slnx that failed (SDK too old for --format) quietly falls back to classic;
        // an EXPLICITLY requested format that failed surfaces the dotnet error instead.
        if ((!ok || created is null) && format == "slnx" && !formatExplicit)
        {
            (ok, log) = await DotnetCli.RunAsync(dir, default, "new", "sln", "-n", name, "-o", dir);
            created = PathIf(".sln") ?? PathIf(".slnx");
        }
        if (!ok || created is null)
            return (null, false, Cs4AiResult.FileError($"session: 'dotnet new sln' failed in '{dir}':\n{log}"));

        var configPath = Cs4AiConfig.PathFor(RepoRoot.From(created));
        if (!File.Exists(configPath))
            File.WriteAllText(configPath, Cs4AiConfig.Preset("sa1201").ToJson() + "\n", new UTF8Encoding(false));

        return (created, true, null);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  In-process dispatch — the daemon's behavior without the daemon.
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<Cs4AiResult> ExecuteInProcessAsync(
        string verb, string[] rest, string? slnx, string? session, string? stdinText)
    {
        SolutionHost? host = null;
        if (slnx is not null)
        {
            if (!File.Exists(slnx))
                return Cs4AiResult.FileError($"file not found: {slnx}");
            var full = Path.GetFullPath(slnx);
            if (!_hosts.TryGetValue(full, out host))
                _hosts[full] = host = new SolutionHost(full);
        }
        else
        {
            // Session-only routing: find the host whose pipe key matches the token prefix.
            var key = DaemonProtocol.PipeKeyFromSessionToken(session!);
            host = key is null ? null
                : _hosts.Values.FirstOrDefault(h =>
                    string.Equals(h.PipeKey, key, StringComparison.OrdinalIgnoreCase));
            if (host is null)
                return Cs4AiResult.NoSession(string.Join("\n",
                    "no-session: no host for this session token (expired or never started)",
                    "recovery: run `cs4ai session <solution>` for a fresh token, then re-fire this command with it") + "\n");
        }

        if (verb == "stop-daemon")
        {
            await host.DisposeAsync();
            _hosts.Remove(host.SlnxPath);
            return Cs4AiResult.Ok($"daemon for '{host.SlnxPath}' stopped (in-process host disposed).\n");
        }

        // The host strips the slnx positional itself; pass the original argv shape through.
        var stdinReader = stdinText is null ? null : new StringReader(stdinText);
        return await host.HandleAsync(new[] { verb }.Concat(rest).ToArray(), stdinReader);
    }

    private static bool IsHelpFlag(string arg) => arg is "-h" or "--help" or "help";

    // ─────────────────────────────────────────────────────────────────────────────
    //  Meta paths
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The README, embedded in the assembly so <c>cs4ai --show-readme</c> needs no
    /// external file. Same pattern as md4ai.</summary>
    internal static string EmbeddedReadme()
    {
        using var stream = typeof(Cs4AiEngine).Assembly
            .GetManifestResourceStream("cs4ai.README.md");
        if (stream is null) return "error: README resource not found in this build.\n";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Write the Claude Code skill (<c>SKILL.md</c>). <paramref name="target"/> may be null
    /// (writes to <c>./.claude/skills/cs4ai/SKILL.md</c>), a path ending in <c>.md</c> (exact
    /// destination), or a directory (treated as a skills root: writes
    /// <c>&lt;dir&gt;/cs4ai/SKILL.md</c>). The skill content is a const string embedded in this
    /// assembly, so skill and binary ship together and can't drift (Settled #26).
    /// </summary>
    internal static Cs4AiResult CreateSkill(string? target)
    {
        string file;
        if (target is null)
            file = Path.Combine(".claude", "skills", "cs4ai", "SKILL.md");
        else if (target.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            file = target;
        else
            file = Path.Combine(target, "cs4ai", "SKILL.md");

        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(file));
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(file, Help.SkillFile, new UTF8Encoding(false));
            return Cs4AiResult.Ok($"Wrote cs4ai skill to {file}\n");
        }
        catch (Exception e)
        {
            return Cs4AiResult.FileError($"could not write skill to '{file}': {e.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.Values)
            await host.DisposeAsync();
        _hosts.Clear();
    }
}
