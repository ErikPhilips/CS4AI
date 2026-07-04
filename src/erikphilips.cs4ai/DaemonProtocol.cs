using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Wire protocol between the CLI shell and the per-solution daemon: one JSON line request, one
/// JSON line response, one connection per call, over a named pipe keyed by the solution path.
/// See the design doc's "Daemon vs. One-Shot" section.
/// <para>
/// The pipe key doubles as the session-token prefix — "the token carries the solution"
/// (Settled #45): a client holding only <c>--session k7f3a9ab12cd.deadbeef…</c> derives the pipe
/// name from the prefix and routes without the `.slnx` positional.
/// </para>
/// </summary>
internal static class DaemonProtocol
{
    /// <summary>JSON request: the argv (verbatim, minus the CLI-level routing) plus any stdin
    /// content the shell captured (stdin can't cross the pipe as a stream; it's small text).
    /// The client's version rides along so a daemon left over from an older install can detect
    /// the upgrade, exit, and let the client respawn a fresh one.</summary>
    public sealed record Request(
        [property: JsonPropertyName("args")] string[] Args,
        [property: JsonPropertyName("stdin")] string? Stdin,
        [property: JsonPropertyName("version")] string? Version = null);

    /// <summary>Error marker a stale daemon returns before exiting so the client respawns.</summary>
    public const string VersionMismatchMarker = "cs4ai-daemon-version-mismatch";

    public sealed record Response(
        [property: JsonPropertyName("exitCode")] int ExitCode,
        [property: JsonPropertyName("fileEdited")] bool FileEdited,
        [property: JsonPropertyName("output")] string? Output,
        [property: JsonPropertyName("error")] string? Error)
    {
        public Cs4AiResult ToResult() => new(ExitCode, FileEdited, Output, Error);
        public static Response From(Cs4AiResult r) => new(r.ExitCode, r.FileEdited, r.Output, r.Error);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOpts);
    public static T? Deserialize<T>(string line) => JsonSerializer.Deserialize<T>(line, JsonOpts);

    /// <summary>
    /// The per-solution key: first 12 hex chars of SHA-256 over the lowercased, normalized
    /// absolute solution path. Names the pipe AND prefixes every session token, which is what
    /// makes tokens self-routing.
    /// </summary>
    public static string PipeKeyFor(string slnxPath)
    {
        var normalized = Path.GetFullPath(slnxPath)
            .Replace('\\', '/')
            .ToLowerInvariant()
            .TrimEnd('/');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    public static string PipeNameFor(string pipeKey) => $"cs4ai-{pipeKey}";

    /// <summary>The v2 session-token prefix (version2.md, <i>Tokens</i>) — distinguishes a session
    /// token from a type token (<c>type_</c>) per-slot.</summary>
    public const string SessionTokenPrefix = "sess_";

    /// <summary>Mint a session token: <c>sess_&lt;pipekey&gt;.&lt;random&gt;</c>. The pipe key prefix
    /// is what makes the token self-routing ("the token carries the solution").</summary>
    public static string NewSessionToken(string pipeKey, string random) =>
        $"{SessionTokenPrefix}{pipeKey}.{random}";

    /// <summary>Extract the pipe key from a session token (<c>sess_&lt;key&gt;.&lt;random&gt;</c>).
    /// Null when the token doesn't have the expected shape.</summary>
    public static string? PipeKeyFromSessionToken(string sessionToken)
    {
        if (!sessionToken.StartsWith(SessionTokenPrefix, StringComparison.Ordinal)) return null;
        var body = sessionToken[SessionTokenPrefix.Length..];
        var dot = body.IndexOf('.');
        if (dot != 12) return null;
        var key = body[..dot];
        return key.All(Uri.IsHexDigit) ? key.ToLowerInvariant() : null;
    }
}
