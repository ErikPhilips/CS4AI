using System.Text.Json;
using System.Text.Json.Serialization;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The `.cs4aiconfig` content at the repo root. Created by `cs4ai init &lt;preset&gt;` after the
/// agent elicits the user's preference on first contact. Repo-local only — no global, no
/// machine-wide. See the design doc, "Initial Configuration" section.
/// </summary>
public sealed class Cs4AiConfig
{
    /// <summary>"full" | "members" | "format" | "off". Default: "full".</summary>
    [JsonPropertyName("canonicalize")]
    public string Canonicalize { get; set; } = "full";

    /// <summary>Top-to-bottom order within a class. Order matters.</summary>
    [JsonPropertyName("memberOrder")]
    public List<string> MemberOrder { get; set; } = [];

    /// <summary>Within a category, sort by access first. Order matters.</summary>
    [JsonPropertyName("accessOrder")]
    public List<string> AccessOrder { get; set; } = [];

    /// <summary>"name" or "source".</summary>
    [JsonPropertyName("sortWithinGroup")]
    public string SortWithinGroup { get; set; } = "name";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Read a `.cs4aiconfig` from disk. Returns null if the file doesn't exist.</summary>
    /// <exception cref="JsonException">Malformed JSON.</exception>
    /// <remarks>Opens with <see cref="FileShare.ReadWrite"/> so the read coexists with the
    /// daemon's held write lock (Settled #30 — the daemon owns the file; readers proceed).</remarks>
    public static Cs4AiConfig? TryLoad(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ".cs4aiconfig");
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Cs4AiConfig>(reader.ReadToEnd(), JsonOpts);
    }

    /// <summary>Path the config would live at for a given repo root.</summary>
    public static string PathFor(string repoRoot) => Path.Combine(repoRoot, ".cs4aiconfig");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    // ─────────────────────────────────────────────────────────────────────────────
    //  Presets — see the design doc, "Preset definitions" subsection.
    // ─────────────────────────────────────────────────────────────────────────────

    public static Cs4AiConfig Preset(string name) => name switch
    {
        "sa1201"    => Sa1201(),
        "microsoft" => Microsoft(),
        "source"    => Source(),
        _           => throw new ArgumentException(
            $"unknown preset '{name}'. Choices: sa1201, microsoft, source.")
    };

    public static IReadOnlyList<(string name, string description)> Presets =>
    [
        ("sa1201",    "StyleCop SA1201 — CodeMaid default, what most modern .NET codebases use"),
        ("microsoft", "Microsoft Framework Design Guidelines — constants → properties → methods"),
        ("source",    "Source order — no reordering on writes; just format"),
    ];

    private static Cs4AiConfig Sa1201() => new()
    {
        Canonicalize = "full",
        MemberOrder =
        [
            "const", "field-static", "field-instance", "constructor", "destructor",
            "delegate", "event", "enum", "interface", "property", "indexer", "method",
            "nested-struct", "nested-class", "nested-record",
        ],
        AccessOrder = ["public", "internal", "protected-internal", "protected", "private-protected", "private"],
        SortWithinGroup = "name",
    };

    private static Cs4AiConfig Microsoft() => new()
    {
        Canonicalize = "full",
        MemberOrder =
        [
            "const", "field-static", "field-instance", "constructor", "property", "indexer",
            "event", "method", "delegate", "enum", "interface",
            "nested-struct", "nested-class", "nested-record",
        ],
        AccessOrder = ["public", "internal", "protected-internal", "protected", "private-protected", "private"],
        SortWithinGroup = "name",
    };

    private static Cs4AiConfig Source() => new()
    {
        Canonicalize = "format",
        MemberOrder = [],
        AccessOrder = [],
        SortWithinGroup = "source",
    };
}

/// <summary>
/// Locate the repo root for a `.slnx` / `.csproj` positional. A `.csproj` usually lives deep
/// inside the repo (src/Project/Project.csproj), so "the directory containing the positional"
/// is wrong for it — the `.cs4aiconfig` lives at the REPO root (Settled #29: one home per
/// repo). Discovery walks up from the positional's directory:
/// <list type="number">
///   <item>nearest ancestor (including self) that already has a `.cs4aiconfig` — wins;</item>
///   <item>else nearest ancestor that looks like a repo root (`.git`, or a `.sln`/`.slnx`);</item>
///   <item>else the positional's own directory (a bare project with no repo around it).</item>
/// </list>
/// </summary>
internal static class RepoRoot
{
    public static string From(string slnxOrCsprojPath)
    {
        var start = Path.GetDirectoryName(Path.GetFullPath(slnxOrCsprojPath))
            ?? throw new ArgumentException($"could not resolve repo root from '{slnxOrCsprojPath}'");

        for (var dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, ".cs4aiconfig")))
                return dir;

        for (var dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, ".git"))
                || Directory.EnumerateFiles(dir, "*.slnx").Any()
                || Directory.EnumerateFiles(dir, "*.sln").Any())
                return dir;

        return start;
    }
}
