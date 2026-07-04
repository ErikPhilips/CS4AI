using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Shell out to <c>dotnet build</c> / <c>dotnet test</c> for the **full-build truth source** (the
/// pivot: graph edits and <c>verify</c> need the project/restore layer — NU*/MSBuild — that Roslyn's
/// in-memory compilation can't see). Parses every error + warning (CS*, NU*, MSB*) from the build
/// output, then runs tests when the build passed. Edits write through to disk, so this always runs
/// against the real working tree (no shadow copy).
/// </summary>
internal static class BuildAndTest
{
    /// <summary>One parsed build diagnostic. <see cref="Line"/> is 0 for project-level diagnostics
    /// (NU*/MSB* restore/target warnings carry no source position).</summary>
    public readonly record struct Diag(string Severity, string Code, string Path, int Line, string Message);

    public readonly record struct Result(
        bool BuildPassed,
        bool TestsPassed,
        IReadOnlyList<Diag> Diagnostics,
        IReadOnlyList<string> TestFailures,
        int FailedCount,
        int TotalTests,
        string Raw = "");   // verbatim dotnet build (+ test) transcript — surfaced by `verify --raw`

    /// <summary>Build + test (used by run-test / verify / the test baseline). The test verdict never
    /// rides on the process exit code alone: a nonzero exit <b>or</b> a summary reporting failures
    /// (or a parsed failure name) counts as red — so a false 0 can't paint a red suite green.</summary>
    public static async Task<Result> RunAsync(string slnxPath, CancellationToken ct = default)
    {
        var (passed, diagnostics, buildRaw) = await BuildOnlyAsync(slnxPath, ct);
        if (!passed)
            return new Result(false, false, diagnostics, [], 0, 0, "── dotnet build ──\n" + buildRaw);

        var test = await RunDotnetAsync(["test", slnxPath, "--nologo", "--no-build"], ct);
        var output = test.stdout + "\n" + test.stderr;
        var failures = ExtractTestFailures(output);
        var failedCount = Math.Max(SumMatches(FailedSummaryRegex, output), failures.Count);
        var total = SumMatches(TotalSummaryRegex, output);
        var testsPassed = test.exitCode == 0 && failedCount == 0;
        return new Result(true, testsPassed, diagnostics, failures, failedCount, total,
            "── dotnet build ──\n" + buildRaw + "\n── dotnet test ──\n" + output);
    }

    /// <summary>Just <c>dotnet build</c> — for seeding/refreshing the build baseline, where tests
    /// aren't needed (session open, graph-edit reload). Returns whether it built, all diagnostics,
    /// and the verbatim transcript.</summary>
    public static async Task<(bool passed, IReadOnlyList<Diag> diagnostics, string raw)> BuildOnlyAsync(
        string slnxPath, CancellationToken ct = default)
    {
        var build = await RunDotnetAsync(["build", slnxPath, "--nologo"], ct);
        var raw = build.stdout + "\n" + build.stderr;
        return (build.exitCode == 0, ExtractDiagnostics(raw), raw);
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunDotnetAsync(
        IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    // MSBuild "C:\file.cs(12,5): error CSxxxx: message [proj]" — diagnostics with a source position.
    private static readonly Regex PositionedRegex = new(
        @"^\s*(?<path>.+?)\((?<line>\d+),\d+\):\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+):\s*(?<msg>.*?)(?:\s*\[[^\]]*\])?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Project-level "C:\proj.csproj : warning NU1510: message [proj]" — NU*/MSB* with no position.
    // The path class must exclude newlines: [^()] alone let the lazy group anchor on an earlier
    // restore-chatter line ("Determining projects to restore...") and swallow it into the path,
    // so chatter rendered inside diagnostic entries and churned the new-vs-preexisting delta.
    private static readonly Regex ProjectLevelRegex = new(
        @"^\s*(?<path>[^()\r\n]+?)\s+:\s*(?<sev>error|warning)\s+(?<code>[A-Za-z]+\d+):\s*(?<msg>.*?)(?:\s*\[[^\]]*\])?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static IReadOnlyList<Diag> ExtractDiagnostics(string output)
    {
        var diags = new List<Diag>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string sev, string code, string path, int line, string msg)
        {
            var key = $"{code}|{path}|{line}|{msg}";
            if (seen.Add(key)) diags.Add(new Diag(sev, code, path, line, msg.Trim()));
        }

        foreach (Match m in PositionedRegex.Matches(output))
            Add(m.Groups["sev"].Value, m.Groups["code"].Value, m.Groups["path"].Value.Trim(),
                int.TryParse(m.Groups["line"].Value, out var ln) ? ln : 0, m.Groups["msg"].Value);

        foreach (Match m in ProjectLevelRegex.Matches(output))
        {
            var path = m.Groups["path"].Value.Trim();
            if (path.Contains('(')) continue; // a positioned line already handled above
            Add(m.Groups["sev"].Value, m.Groups["code"].Value, path, 0, m.Groups["msg"].Value);
        }

        return diags;
    }

    // The per-failure line: "  Failed Ledger.Tests.WalletTests.Intentionally_Fails [2 ms]". The name
    // is a dotted FQTN; the trailing " [n ms]" (or nothing) bounds it. Guard against matching the
    // summary line "Failed:     1," by requiring the name to start with a letter/underscore (the
    // summary has "Failed:" — a colon, no space — so it never reaches this).
    private static readonly Regex TestFailRegex = new(
        @"(?:^|\s)Failed\s+(?<name>[A-Za-z_][\w.<>+`,]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    // Summary counts, e.g. "Failed!  - Failed:     1, Passed:     3, Skipped: 0, Total:     4". One
    // per test project — summed across projects. The colon distinguishes these from per-failure lines.
    private static readonly Regex FailedSummaryRegex = new(@"\bFailed:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex TotalSummaryRegex = new(@"\bTotal:\s*(\d+)", RegexOptions.Compiled);

    private static int SumMatches(Regex re, string output)
    {
        var sum = 0;
        foreach (Match m in re.Matches(output))
            if (int.TryParse(m.Groups[1].Value, out var n)) sum += n;
        return sum;
    }

    private static IReadOnlyList<string> ExtractTestFailures(string output)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in TestFailRegex.Matches(output))
        {
            var name = m.Groups["name"].Value;
            if (seen.Add(name)) failures.Add(name);
        }
        return failures;
    }

    /// <summary>Test failures parsed from a raw <c>dotnet test</c> transcript — exposed for unit
    /// tests that feed captured output without shelling out.</summary>
    internal static IReadOnlyList<string> ParseTestFailures(string output) => ExtractTestFailures(output);

    /// <summary>Diagnostics parsed from a raw <c>dotnet build</c> transcript — exposed for tests.</summary>
    internal static IReadOnlyList<Diag> ParseDiagnostics(string output) => ExtractDiagnostics(output);

    /// <summary>Summed <c>Failed:</c> / <c>Total:</c> counts from a transcript — exposed for tests.</summary>
    internal static (int failed, int total) ParseSummary(string output) =>
        (SumMatches(FailedSummaryRegex, output), SumMatches(TotalSummaryRegex, output));
}
