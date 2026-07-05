using System.Diagnostics;
using System.Text;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Shells the <c>dotnet</c> CLI and captures its combined output. Shared by the structure tier
/// (<see cref="StructureOps"/>) and the <c>session</c> bootstrap — cs4ai offloads project/solution
/// scaffolding to <c>dotnet</c> rather than reinventing the file formats.
/// </summary>
internal static class DotnetCli
{
    public static async Task<(bool ok, string log)> RunAsync(
        string workingDir, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // The daemon runs DETACHED (no console) — without this every dotnet child
            // allocates its own visible console window on the user's desktop.
            CreateNoWindow = true,
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
