namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Shared argv parsing for every verb. One grammar: positionals plus the small disciplined flag
/// set (the design doc's "Flag discipline" subsection — anything else belongs in `.cs4aiconfig`).
/// </summary>
internal sealed class ArgParse
{
    public List<string> Positionals { get; } = [];
    public string? Session;          // --session <t>   — the transaction handle; routes the call
    public string? Token;            // --token <hex>   — the staleness citation
    public string? Depth;            // --depth addresses|signatures|full
    public string? From;             // --from <file>   — body content / stack trace
    public string? Text;             // --text <inline>
    public string? InFile;           // --in-file <file> — partial-type / same-name-arity targeting
    public string? To;               // --to <sig|type> — move target
    public string? Path;             // --path <folder>  — create's decoupled destination folder
    public string? SetBody;          // --set-body <decl> — inline member/type declaration
    public string? SetComment;       // --set-comment <xml> — doc-comment trivia
    public string? SetNamespace;     // --set-namespace <ns>
    public string? SetUsings;        // --set-usings <imports>
    public string? SetAttributes;    // --set-attributes <[A],[B]> — whole-replace the symbol's attributes
    public string? SetFile;          // --set-file <path> — the file the type should live in (rename/update)
    public string? Template;         // --template <t>   — create-project template
    public string? Version;          // --version <v>    — add-reference package version ("Latest" omits)
    public string? Format;           // --format <sln|slnx> — session-create solution format (default slnx)
    public int? LockTimeoutSeconds;  // --lock-timeout <seconds> — commit flush window
    public bool StdinFlag;           // --stdin
    public bool DryRun;              // --dry-run
    public bool Log;                 // --log — session-only: record every command, flush on commit
    public bool Raw;                 // --raw — verify-only: append the verbatim dotnet transcript
    public bool Help;

    /// <summary>Flags that consume the next argv token as their value. Used both here and by
    /// the positional-stripping logic so a flag value is never mistaken for a positional.</summary>
    public static readonly HashSet<string> ValueFlags = new(StringComparer.Ordinal)
    {
        "--session", "--token", "--depth", "--from", "--text", "--in-file", "--to", "--path",
        "--set-body", "--set-comment", "--set-namespace", "--set-usings", "--set-attributes",
        "--set-file", "--template", "--version", "--format", "--lock-timeout",
    };

    public static ArgParse Parse(string[] args)
    {
        var p = new ArgParse();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help": p.Help = true; break;
                case "--session" when i + 1 < args.Length: p.Session = args[++i]; break;
                case "--token"   when i + 1 < args.Length: p.Token   = args[++i]; break;
                case "--depth"   when i + 1 < args.Length: p.Depth   = args[++i]; break;
                case "--from"    when i + 1 < args.Length: p.From    = args[++i]; break;
                case "--text"    when i + 1 < args.Length: p.Text    = args[++i]; break;
                case "--in-file" when i + 1 < args.Length: p.InFile  = args[++i]; break;
                case "--to"      when i + 1 < args.Length: p.To      = args[++i]; break;
                case "--path"    when i + 1 < args.Length: p.Path    = args[++i]; break;
                case "--set-body"      when i + 1 < args.Length: p.SetBody      = args[++i]; break;
                case "--set-comment"   when i + 1 < args.Length: p.SetComment   = args[++i]; break;
                case "--set-namespace" when i + 1 < args.Length: p.SetNamespace = args[++i]; break;
                case "--set-usings"    when i + 1 < args.Length: p.SetUsings    = args[++i]; break;
                case "--set-attributes" when i + 1 < args.Length: p.SetAttributes = args[++i]; break;
                case "--set-file" when i + 1 < args.Length: p.SetFile = args[++i]; break;
                case "--template" when i + 1 < args.Length: p.Template = args[++i]; break;
                case "--version"  when i + 1 < args.Length: p.Version  = args[++i]; break;
                case "--format"   when i + 1 < args.Length: p.Format   = args[++i]; break;
                case "--lock-timeout" when i + 1 < args.Length:
                    p.LockTimeoutSeconds = int.TryParse(args[++i], out var s) ? s : null; break;
                case "--stdin":   p.StdinFlag = true; break;
                case "--dry-run": p.DryRun = true; break;
                case "--log":     p.Log = true; break;
                case "--raw":     p.Raw = true; break;
                default: p.Positionals.Add(args[i]); break;
            }
        }
        return p;
    }

    /// <summary>Does this argv token look like a solution/project path?</summary>
    public static bool LooksLikeSolution(string arg) =>
        arg.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
        || arg.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || arg.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Remove the slnx/csproj positional from an argv slice, if present, skipping flag values so
    /// `--from new.sln` is never misread as routing. Returns the remaining args and the path.
    /// The slnx positional is required on session-less calls and optional (must agree) on
    /// session-bearing ones — Settled #45.
    /// </summary>
    public static (string[] rest, string? slnx) StripSolutionPositional(string[] args)
    {
        string? slnx = null;
        var rest = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
        {
            if (ValueFlags.Contains(args[i]) && i + 1 < args.Length)
            {
                rest.Add(args[i]);
                rest.Add(args[++i]);
                continue;
            }
            if (slnx is null && !args[i].StartsWith('-') && LooksLikeSolution(args[i]))
            {
                slnx = args[i];
                continue;
            }
            rest.Add(args[i]);
        }
        return (rest.ToArray(), slnx);
    }

    /// <summary>Find the --session value in raw argv without full parsing (CLI routing).</summary>
    public static string? FindSession(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--session")
                return args[i + 1];
        return null;
    }

    public async Task<(string? text, Cs4AiResult? error)> ReadContentAsync(TextReader? stdin)
    {
        if (Text is not null) return (Text, null);
        if (From is not null)
        {
            if (!File.Exists(From)) return (null, Cs4AiResult.FileError($"--from file not found: {From}"));
            return (await File.ReadAllTextAsync(From), null);
        }
        // stdin is opt-in: read it only on an explicit --stdin, never merely because a reader exists.
        if (StdinFlag)
            return (await (stdin?.ReadToEndAsync() ?? Task.FromResult("")), null);
        return (null, null);
    }
}
