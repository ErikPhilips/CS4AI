using ErikPhilips.Cs4Ai;
using Xunit;

namespace ErikPhilips.Cs4Ai.Tests;

/// <summary>
/// Bug #4 regression: run-test/verify must report a red suite as red. These cover the two seams that
/// produce the verdict without shelling out — the absolute-verdict decision
/// (<see cref="SolutionHost.TestVerdictLines"/>) and the transcript parser
/// (<see cref="BuildAndTest"/>). The headline case is a failing test that is ALREADY in the baseline:
/// the old delta model reported "passed"; the fix reports "failed".
/// </summary>
public class TestVerdictTests
{
    private static readonly IReadOnlySet<string> NoBaseline = new HashSet<string>();

    private static BuildAndTest.Result Run(bool buildPassed, bool testsPassed,
        IReadOnlyList<string> failures, int failedCount, int total) =>
        new(buildPassed, testsPassed, [], failures, failedCount, total);

    [Fact]
    public void RedSuite_FailureAlreadyInBaseline_StillReportsFailed()   // the bug #4 repro
    {
        var baseline = new HashSet<string> { "Ledger.Tests.WalletTests.Intentionally_Fails" };
        var run = Run(true, false, ["Ledger.Tests.WalletTests.Intentionally_Fails"], 1, 4);

        var (lines, last) = SolutionHost.TestVerdictLines(run, baseline);

        Assert.Equal("red", last);
        Assert.Contains(lines, l => l.StartsWith("tests: failed"));
        Assert.Contains(lines, l => l.Contains("1 failing"));
        Assert.Contains(lines, l => l.Contains("(preexisting)")); // attributed, not hidden
        Assert.DoesNotContain(lines, l => l.Contains("tests: passed"));
    }

    [Fact]
    public void ParseDiagnostics_RestoreChatter_NeverBecomesADiagnostic()
    {
        // Field bug: [^()] in the project-level regex spans newlines, so the lazy path group
        // anchored on restore chatter and swallowed it into the NU1510 entry's path — phantom
        // `+`-tagged diagnostics that churned the new-vs-preexisting delta on every run.
        const string transcript = """
              Determining projects to restore...
              All projects are up-to-date for restore.
            C:\repo\src\Fixture.csproj : warning NU1510: PackageReference System.Text.Json will not be pruned.
            C:\repo\src\Calc.cs(12,5): error CS0246: The type or namespace name 'Baz' could not be found
                0 Warning(s)
            """;

        var diags = BuildAndTest.ParseDiagnostics(transcript);

        Assert.Equal(2, diags.Count);
        var nu = Assert.Single(diags, d => d.Code == "NU1510");
        Assert.Equal(@"C:\repo\src\Fixture.csproj", nu.Path); // path is ONE line — no chatter
        Assert.DoesNotContain("Determining", nu.Path);
        var cs = Assert.Single(diags, d => d.Code == "CS0246");
        Assert.Equal(12, cs.Line);
    }

    [Fact]
    public void RedSuite_NewFailure_ReportedAsNew()
    {
        var run = Run(true, false, ["Ns.T.Fails"], 1, 4);
        var (lines, last) = SolutionHost.TestVerdictLines(run, NoBaseline);

        Assert.Equal("red", last);
        Assert.Contains(lines, l => l.Contains("1 new, 0 preexisting"));
        Assert.Contains(lines, l => l == "failed-test: Ns.T.Fails");
    }

    [Fact]
    public void GreenSuite_ReportsPassedWithCount()
    {
        var run = Run(true, true, [], 0, 4);
        var (lines, last) = SolutionHost.TestVerdictLines(run, NoBaseline);

        Assert.Equal("green", last);
        Assert.Contains(lines, l => l.StartsWith("tests: passed"));
        Assert.Contains(lines, l => l.Contains("4 run"));
    }

    [Fact]
    public void BuildFailed_ReportsSkipped()
    {
        var run = Run(false, false, [], 0, 0);
        var (lines, last) = SolutionHost.TestVerdictLines(run, NoBaseline);

        Assert.Equal("none", last);
        Assert.Contains(lines, l => l.Contains("skipped (build failed)"));
    }

    [Fact]
    public void NonzeroExit_NoParsedFailures_StillRed()   // a crashed/aborted run can't read green
    {
        var run = Run(true, false, [], 0, 0);
        var (lines, last) = SolutionHost.TestVerdictLines(run, NoBaseline);

        Assert.Equal("red", last);
        Assert.Contains(lines, l => l.StartsWith("tests: failed"));
    }

    [Fact]
    public void NoTestsRan_IsDistinctFromPassed()
    {
        var run = Run(true, true, [], 0, 0);
        var (lines, last) = SolutionHost.TestVerdictLines(run, NoBaseline);

        Assert.Equal("none", last);
        Assert.Contains(lines, l => l.Contains("no tests ran"));
    }

    // ── transcript parser (real .NET 10 dotnet test output) ──────────────────────────────────────

    private const string FailingOutput = """
        Test run for C:\repos\_harness\Ledger.Tests\bin\Debug\net10.0\Ledger.Tests.dll (.NETCoreApp,Version=v10.0)
        [xUnit.net 00:00:00.11]     Ledger.Tests.WalletTests.Intentionally_Fails [FAIL]
          Failed Ledger.Tests.WalletTests.Intentionally_Fails [2 ms]
          Error Message:
           Assert.Equal() Failure: Values differ
        Failed!  - Failed:     1, Passed:     3, Skipped:     0, Total:     4, Duration: 16 ms - Ledger.Tests.dll (net10.0)
        """;

    private const string PassingOutput = """
        Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 12 ms - Ledger.Tests.dll (net10.0)
        """;

    [Fact]
    public void ParseTestFailures_ExtractsName_NotSummaryDigits()
    {
        var failures = BuildAndTest.ParseTestFailures(FailingOutput);
        Assert.Single(failures);
        Assert.Equal("Ledger.Tests.WalletTests.Intentionally_Fails", failures[0]);
    }

    [Fact]
    public void ParseSummary_SumsFailedAndTotal()
    {
        Assert.Equal((1, 4), BuildAndTest.ParseSummary(FailingOutput));
        Assert.Equal((0, 4), BuildAndTest.ParseSummary(PassingOutput));
    }

    [Fact]
    public void ParseSummary_MultipleProjects_Sums()
    {
        var two = FailingOutput + "\n" + FailingOutput;
        Assert.Equal((2, 8), BuildAndTest.ParseSummary(two));
    }
}
