using System.Text;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The structure tier (version2.md, <i>Structure tier</i>): container ops that shell <c>dotnet</c> /
/// rewrite project files, <b>write disk immediately and reload the workspace</b>. Session-gated for
/// routing but <b>not staged</b> — <c>discard</c> can't undo them, and they <b>refuse if staged edits
/// are pending</b> (a reload would orphan the fork). Distinct verbs from the semantic family; never
/// overloaded across the boundary.
/// <para>
/// Writes return reads at container grain: each result echoes the full affected <c>.csproj</c>
/// contents, and the solution file as a <c>+</c>/<c>-</c> line delta (omitted when untouched),
/// so the agent sees exactly what landed without the GUID soup.
/// </para>
/// </summary>
internal static class StructureOps
{
    public static async Task<Cs4AiResult> RunAsync(
        SolutionHost host, string verb, string[] rest, CancellationToken ct)
    {
        var p = ArgParse.Parse(rest);
        if (p.Positionals.Count == 0)
            return Cs4AiResult.UsageError($"{verb}: missing session token — lead with the sess_ token.");

        var session = host.ValidateSession(p.Positionals[0]);
        if (session is null)
            return await host.NoSessionRefusalAsync(p.Positionals[0], null, ct);

        // Snapshot the solution file up front: the result echoes it as a DELTA (the full .sln is
        // GUID soup the agent never reads), so every op needs the before-text.
        var slnBefore = SafeRead(host.SlnxPath);

        // No staged fork to orphan — edits already wrote through to disk. The reload that follows a
        // graph edit just re-reads the (already-consistent) tree from disk.
        return verb switch
        {
            "create-project"  => await CreateProjectAsync(host, p, slnBefore, ct),
            "delete-project"  => await DeleteProjectAsync(host, p, slnBefore, ct),
            "add-reference"   => await AddReferenceAsync(host, p, slnBefore, ct),
            "delete-reference"=> await DeleteReferenceAsync(host, p, slnBefore, ct),
            "update-project"  => await UpdateProjectAsync(host, p, slnBefore, ct),
            _ => Cs4AiResult.UsageError($"unknown structure verb '{verb}'."),
        };
    }

    // ── create-project ────────────────────────────────────────────────────────────────────────

    private static async Task<Cs4AiResult> CreateProjectAsync(SolutionHost host, ArgParse p, string slnBefore, CancellationToken ct)
    {
        if (p.Positionals.Count < 2)
            return Cs4AiResult.UsageError(
                "create-project: usage: cs4ai create-project <sess> <project-name> --path <dir> [--template <t>]");
        var name = p.Positionals[1];
        var template = p.Template ?? "classlib";
        var outDir = p.Path ?? name;
        var absOut = Path.GetFullPath(Path.Combine(host.RepoRootPath, outDir));

        var (ok1, log1) = await DotnetCli.RunAsync(host.RepoRootPath, ct,
            "new", template, "-n", name, "-o", absOut);
        if (!ok1) return Cs4AiResult.FileError($"create-project: dotnet new failed:\n{log1}");

        var csproj = Path.Combine(absOut, name + ".csproj");
        var (ok2, log2) = await DotnetCli.RunAsync(host.RepoRootPath, ct, "sln", host.SlnxPath, "add", csproj);
        if (!ok2) return Cs4AiResult.FileError($"create-project: dotnet sln add failed:\n{log2}");

        await host.ReloadAndRefreshSessionAsync(ct);
        return ContainerResult(host, "create-project", new[] { csproj }, slnBefore);
    }

    // ── delete-project ────────────────────────────────────────────────────────────────────────

    private static async Task<Cs4AiResult> DeleteProjectAsync(SolutionHost host, ArgParse p, string slnBefore, CancellationToken ct)
    {
        if (p.Positionals.Count < 2)
            return Cs4AiResult.UsageError("delete-project: usage: cs4ai delete-project <sess> <project.csproj>");
        var csproj = ResolvePath(host, p.Positionals[1]);
        var (ok, log) = await DotnetCli.RunAsync(host.RepoRootPath, ct, "sln", host.SlnxPath, "remove", csproj);
        if (!ok) return Cs4AiResult.FileError($"delete-project: dotnet sln remove failed:\n{log}");
        await host.ReloadAndRefreshSessionAsync(ct);
        return ContainerResult(host, "delete-project", [], slnBefore);
    }

    // ── add-reference / delete-reference ────────────────────────────────────────────────────────

    private static async Task<Cs4AiResult> AddReferenceAsync(SolutionHost host, ArgParse p, string slnBefore, CancellationToken ct)
    {
        if (p.Positionals.Count < 3)
            return Cs4AiResult.UsageError(
                "add-reference: usage: cs4ai add-reference <sess> <project.csproj> <package-id|ref.csproj> [--version <v>]");
        var target = ResolvePath(host, p.Positionals[1]);
        var reference = p.Positionals[2];

        bool isProjectRef = reference.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
        var (ok, log) = isProjectRef
            ? await DotnetCli.RunAsync(host.RepoRootPath, ct, "add", target, "reference", ResolvePath(host, reference))
            : p.Version is { } v && !v.Equals("Latest", StringComparison.OrdinalIgnoreCase)
                ? await DotnetCli.RunAsync(host.RepoRootPath, ct, "add", target, "package", reference, "--version", v)
                : await DotnetCli.RunAsync(host.RepoRootPath, ct, "add", target, "package", reference);
        if (!ok) return Cs4AiResult.FileError($"add-reference: dotnet add failed:\n{log}");

        await host.ReloadAndRefreshSessionAsync(ct);
        return ContainerResult(host, "add-reference", new[] { target }, slnBefore);
    }

    private static async Task<Cs4AiResult> DeleteReferenceAsync(SolutionHost host, ArgParse p, string slnBefore, CancellationToken ct)
    {
        if (p.Positionals.Count < 3)
            return Cs4AiResult.UsageError(
                "delete-reference: usage: cs4ai delete-reference <sess> <project.csproj> <package-id|ref.csproj>");
        var target = ResolvePath(host, p.Positionals[1]);
        var reference = p.Positionals[2];

        bool isProjectRef = reference.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
        var (ok, log) = isProjectRef
            ? await DotnetCli.RunAsync(host.RepoRootPath, ct, "remove", target, "reference", ResolvePath(host, reference))
            : await DotnetCli.RunAsync(host.RepoRootPath, ct, "remove", target, "package", reference);
        if (!ok) return Cs4AiResult.FileError($"delete-reference: dotnet remove failed:\n{log}");

        await host.ReloadAndRefreshSessionAsync(ct);
        return ContainerResult(host, "delete-reference", new[] { target }, slnBefore);
    }

    // ── update-project (whole-csproj rewrite — the MSBuild escape hatch) ─────────────────────────

    private static async Task<Cs4AiResult> UpdateProjectAsync(SolutionHost host, ArgParse p, string slnBefore, CancellationToken ct)
    {
        if (p.Positionals.Count < 2 || p.SetBody is null)
            return Cs4AiResult.UsageError(
                "update-project: usage: cs4ai update-project <sess> <project.csproj> --set-body <whole-xml>");
        var csproj = ResolvePath(host, p.Positionals[1]);
        if (!File.Exists(csproj))
            return Cs4AiResult.FileError($"update-project: not found: {host.Relativize(csproj)}");

        await File.WriteAllTextAsync(csproj, p.SetBody.TrimEnd() + "\n", new UTF8Encoding(false), ct);
        await host.ReloadAndRefreshSessionAsync(ct);
        return ContainerResult(host, "update-project", new[] { csproj }, slnBefore);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The container-grain "write returns the read", in the <b>same framed grammar as a
    /// semantic edit</b> (so the agent learns one format): a status line, container frames, then the
    /// <c>[build …]</c> block. The affected csprojs echo in FULL (small, and the file the op actually
    /// targets); the solution file echoes as a <c>+</c>/<c>-</c> line DELTA — the full .sln is GUID
    /// soup the agent never reads — and is omitted entirely when the op didn't touch it. The build
    /// axis is refreshed by the preceding reload against the frozen baseline, so a structure op that
    /// broke the load (e.g. add-reference → NU1510) shows up as a <c>new</c> error.</summary>
    private static Cs4AiResult ContainerResult(
        SolutionHost host, string op, IReadOnlyList<string> csprojs, string slnBefore)
    {
        var lines = new List<string> { "ok" };

        var slnDelta = LineDelta(slnBefore, SafeRead(host.SlnxPath));
        if (slnDelta.Count > 0)
            lines.AddRange(FrameRenderer.ContainerFrame(
                op, Path.GetFileNameWithoutExtension(host.SlnxPath), host.Relativize(host.SlnxPath),
                string.Join("\n", slnDelta)));

        foreach (var c in csprojs.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            lines.AddRange(FrameRenderer.ContainerFrame(
                op, Path.GetFileNameWithoutExtension(c), host.Relativize(c), SafeRead(c)));

        if (host.ActiveSession?.CachedOutcome is { } outcome)
            lines.AddRange(FrameRenderer.BuildBlock(outcome));

        return Cs4AiResult.Edited(string.Join("\n", lines) + "\n");
    }

    /// <summary>Order-preserving multiset line diff: `- ` lines that left, `+ ` lines that arrived.
    /// Positional moves of identical lines don't report (a .sln block shuffle is not a change the
    /// agent cares about).</summary>
    private static List<string> LineDelta(string before, string after)
    {
        static string[] Split(string s) => s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        static Dictionary<string, int> Count(string[] xs)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var x in xs) d[x] = d.GetValueOrDefault(x) + 1;
            return d;
        }

        var beforeLines = Split(before);
        var afterLines = Split(after);
        var inAfter = Count(afterLines);
        var inBefore = Count(beforeLines);

        var delta = new List<string>();
        foreach (var l in beforeLines)
        {
            if (inAfter.TryGetValue(l, out var n) && n > 0) inAfter[l] = n - 1;
            else if (l.Trim().Length > 0) delta.Add("- " + l);
        }
        foreach (var l in afterLines)
        {
            if (inBefore.TryGetValue(l, out var n) && n > 0) inBefore[l] = n - 1;
            else if (l.Trim().Length > 0) delta.Add("+ " + l);
        }
        return delta;
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch (Exception e) { return $"<unreadable: {e.Message}>"; }
    }

    private static string ResolvePath(SolutionHost host, string maybeRelative) =>
        Path.IsPathRooted(maybeRelative)
            ? maybeRelative
            : Path.GetFullPath(Path.Combine(host.RepoRootPath, maybeRelative));
}
