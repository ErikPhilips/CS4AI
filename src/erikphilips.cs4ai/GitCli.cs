using System.Diagnostics;
using System.Text;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Best-effort <c>git</c> invocation for the one place cs4ai wants git's own semantics: relocating a
/// file with <c>git mv</c> so rename detection survives (a plain delete-and-recreate loses history).
/// Modeled on <see cref="DotnetCli"/> — pure process invocation, no dependency. Everything degrades
/// gracefully: not a git work tree, an untracked file, or git-not-installed all resolve to
/// <c>false</c>, and the caller falls back to a plain filesystem rename via the normal write-through.
/// </summary>
internal static class GitCli
{
    /// <summary>Relocate <paramref name="oldPath"/> to <paramref name="newPath"/> via <c>git mv</c>,
    /// staging the rename. Returns true when git actually moved the file (history preserved); false
    /// when it couldn't (untracked / not a repo / git absent) — the caller then does a plain rename.</summary>
    public static async Task<bool> TryMoveAsync(
        string oldPath, string newPath, CancellationToken ct)
    {
        try
        {
            // git mv won't create the destination folder; make it first so an intra-project subfolder
            // move ("Models/Wallet.cs") succeeds.
            Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
            var workingDir = Path.GetDirectoryName(oldPath) ?? Directory.GetCurrentDirectory();
            var (ok, _) = await RunAsync(workingDir, ct, "mv", "--", oldPath, newPath);
            return ok;
        }
        catch
        {
            // git not on PATH (Win32Exception) or any other launch failure → fall back.
            return false;
        }
    }

    private static async Task<(bool ok, string log)> RunAsync(
        string workingDir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode == 0, sb.ToString());
    }
}
