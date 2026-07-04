using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Loads a `.slnx` / `.sln` / `.csproj` via Roslyn's <see cref="MSBuildWorkspace"/>.
/// <para>
/// <b>Exit-code principle (2026-06-29 pivot):</b> the exit code answers only "was the command
/// valid?" — never "did the build/load succeed?" An MSBuild <c>WorkspaceFailed</c> diagnostic (e.g.
/// net10 escalating NU1510 "will not be pruned" to a build error) is a <i>build</i> fact, surfaced
/// through <c>buildOutcome</c> / <c>verify</c>, not the exit code. So a load <b>diagnostic</b> is
/// tolerated (best-effort solution returned); only a hard <b>exception</b> — the inputs can't be read
/// at all — is exit 2. This reverses the transactional-era "abort on any WorkspaceFailed" rule; the
/// tradeoff is that a genuinely half-loaded graph can under-report a cascade, which <c>verify</c>'s
/// real build then catches.
/// </para>
/// </summary>
internal static class WorkspaceLoader
{
    private static bool _msbuildRegistered;

    /// <summary>Register the .NET SDK's MSBuild assemblies. Idempotent. Must be called before
    /// constructing an <see cref="MSBuildWorkspace"/>.</summary>
    public static void EnsureMSBuildLocated()
    {
        if (_msbuildRegistered) return;
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
        _msbuildRegistered = true;
    }

    /// <summary>
    /// Open a solution or project, returning the owning workspace so a long-lived host (the
    /// daemon's <see cref="SolutionHost"/>) can dispose it on reload. Exit 2 on any MSBuild
    /// load failure.
    /// </summary>
    public static async Task<(Workspace? workspace, Solution? solution, Cs4AiResult? error)>
        LoadWithWorkspaceAsync(string slnxOrCsproj, CancellationToken ct = default)
    {
        if (!File.Exists(slnxOrCsproj))
            return (null, null, Cs4AiResult.FileError($"file not found: {slnxOrCsproj}"));

        EnsureMSBuildLocated();

        var workspace = MSBuildWorkspace.Create();

        try
        {
            Solution solution;
            var ext = Path.GetExtension(slnxOrCsproj).ToLowerInvariant();
            if (ext is ".slnx" or ".sln")
                solution = await workspace.OpenSolutionAsync(slnxOrCsproj, cancellationToken: ct);
            else if (ext == ".csproj")
            {
                var project = await workspace.OpenProjectAsync(slnxOrCsproj, cancellationToken: ct);
                solution = project.Solution;
            }
            else
            {
                workspace.Dispose();
                return (null, null, Cs4AiResult.UsageError(
                    $"unrecognized extension '{ext}' — expected .slnx, .sln, or .csproj"));
            }

            // A WorkspaceFailed *diagnostic* (e.g. net10's NU1510-as-error) is a build fact, not a
            // command-validity failure — tolerate it and return the best-effort solution. The build
            // truth reaches the agent through buildOutcome / verify, never the exit code. (Only a hard
            // exception below — inputs unreadable — is exit 2.)
            return (workspace, solution, null);
        }
        catch (Exception e)
        {
            workspace.Dispose();
            return (null, null, Cs4AiResult.FileError(
                $"could not load workspace '{slnxOrCsproj}': {e.Message}"));
        }
    }
}
