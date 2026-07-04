namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The outcome of an <see cref="Cs4AiEngine.ExecuteAsync"/> call. Program.cs maps this to console
/// I/O (stdout / stderr / exit code).
/// <para>
/// Eight exit codes: five inherited from md4ai (ok / usage / file-or-parse / ambiguous /
/// not-found), plus <see cref="CodeStale"/> (5), <see cref="CodeNeedsConfig"/> (6), and
/// <see cref="CodeNoSession"/> (7) — cs4ai's three deliberate divergences. See the design doc's
/// "Result Envelope and Exit Codes" section for why none can collapse into an existing code.
/// </para>
/// <para>
/// <b>Critical:</b> the exit code answers only the question "did the requested operation
/// happen?". Validation state lives in the explicit <c>build</c> / <c>run-test</c> verbs'
/// responses, never collapsed into a write's exit code. See the "Output Is Shaped by the Next
/// Action" section.
/// </para>
/// </summary>
public readonly record struct Cs4AiResult(int ExitCode, bool FileEdited, string? Output, string? Error)
{
    public const int CodeOk          = 0;  // success — the call did its job
    public const int CodeUsage       = 1;  // bad args, unknown command, malformed address
    public const int CodeFileOrParse = 2;  // file/project not found, MSBuild load failed
    public const int CodeAmbiguous   = 3;  // address matched multiple symbols
    public const int CodeNotFound    = 4;  // address matched zero symbols
    public const int CodeStale       = 5;  // write rejected: no valid cited staleness token (stale or omitted)
    public const int CodeNeedsConfig = 6;  // no .cs4aiconfig at repo root; first-contact setup needed
    public const int CodeNoSession   = 7;  // write without a valid edit-session token; refusal hands one back

    public static Cs4AiResult Ok(string? output = null)        => new(CodeOk,          false, output, null);
    public static Cs4AiResult Edited(string? output = null)    => new(CodeOk,          true,  output, null);
    public static Cs4AiResult Usage(string text)               => new(CodeUsage,       false, text,   null);
    public static Cs4AiResult UsageError(string m)             => new(CodeUsage,       false, null,   m);
    public static Cs4AiResult FileError(string m)              => new(CodeFileOrParse, false, null,   m);
    public static Cs4AiResult Ambiguous(string m)              => new(CodeAmbiguous,   false, null,   m);
    public static Cs4AiResult NotFound(string m)               => new(CodeNotFound,    false, null,   m);
    public static Cs4AiResult Stale(string output)             => new(CodeStale,       false, output, null);
    public static Cs4AiResult NeedsConfig(string output)       => new(CodeNeedsConfig, false, output, null);
    public static Cs4AiResult NoSession(string output)         => new(CodeNoSession,   false, output, null);
}
