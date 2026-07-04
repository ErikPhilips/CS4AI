using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// CLI-side routing to the per-solution daemon. The transparent auto-daemon (the design doc's
/// "Daemon vs. One-Shot" section): try to connect; if no daemon exists, spawn one (this same
/// binary with the hidden <c>--daemon</c> argument) and connect to it. The daemon creates its
/// pipe before loading the workspace, so the first call connects immediately and waits on the
/// response while the cold load happens.
/// <para>
/// Routing key: a <c>--session</c> token's prefix when present ("the token carries the
/// solution", Settled #45), otherwise the hash of the `.slnx` positional.
/// </para>
/// </summary>
internal static class DaemonClient
{
    private static readonly TimeSpan ConnectProbe = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SpawnConnectWindow = TimeSpan.FromSeconds(20);
    // Generous ceiling — far above any real op (cold load, verify build+test); rescues a true deadlock.
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Forward a call to the daemon for <paramref name="slnxPath"/>, spawning it if
    /// needed.</summary>
    public static async Task<Cs4AiResult> RouteBySolutionAsync(
        string slnxPath, string[] args, string? stdinText, bool debug = false,
        CancellationToken ct = default)
    {
        if (!File.Exists(slnxPath))
            return Cs4AiResult.FileError($"file not found: {slnxPath}");

        var key = DaemonProtocol.PipeKeyFor(slnxPath);
        var pipe = await TryConnectAsync(key, ConnectProbe, ct);
        if (pipe is null)
        {
            SpawnDaemon(slnxPath, debug);
            pipe = await TryConnectAsync(key, SpawnConnectWindow, ct);
            if (pipe is null)
                return Cs4AiResult.FileError(
                    $"could not start or reach the cs4ai daemon for '{slnxPath}' " +
                    $"(pipe {DaemonProtocol.PipeNameFor(key)}).");
        }

        Cs4AiResult result;
        await using (pipe)
            result = await SendAsync(pipe, args, stdinText, ct);

        // A daemon from an older install detected the version mismatch and exited; respawn
        // fresh and retry once. (Any staged session died with the old daemon — same loss
        // semantics as daemon death, surfaced by the normal exit-7 handshake.)
        if (result.Error == DaemonProtocol.VersionMismatchMarker)
        {
            await Task.Delay(200, ct);
            SpawnDaemon(slnxPath, debug);
            var fresh = await TryConnectAsync(key, SpawnConnectWindow, ct);
            if (fresh is null)
                return Cs4AiResult.FileError(
                    $"daemon for '{slnxPath}' restarted after a version upgrade but could not be reached.");
            await using (fresh)
                return await SendAsync(fresh, args, stdinText, ct);
        }
        return result;
    }

    /// <summary>Forward a call routed purely by session token. No slnx known, so no daemon can
    /// be spawned: a dead pipe means daemon and session are gone — exit 7, fresh handshake (the
    /// design doc's "The session token carries the solution" subsection).</summary>
    public static async Task<Cs4AiResult> RouteBySessionAsync(
        string sessionToken, string[] args, string? stdinText, CancellationToken ct = default)
    {
        var key = DaemonProtocol.PipeKeyFromSessionToken(sessionToken);
        if (key is null)
            return Cs4AiResult.UsageError(
                $"malformed session token '{sessionToken}' — expected <12-hex-key>.<id>.");

        var pipe = await TryConnectAsync(key, ConnectProbe, ct);
        if (pipe is null)
            return Cs4AiResult.NoSession(string.Join("\n",
                "no-session: the daemon holding this session is gone (idle timeout, stop-daemon, or reboot)",
                "edits-safe: every edit was written to disk when you made it — nothing was lost with the daemon",
                "recovery: run `cs4ai session <solution>` for a fresh token, then re-fire this command with it") + "\n");

        Cs4AiResult sessionResult;
        await using (pipe)
            sessionResult = await SendAsync(pipe, args, stdinText, ct);

        if (sessionResult.Error == DaemonProtocol.VersionMismatchMarker)
            return Cs4AiResult.NoSession(string.Join("\n",
                "no-session: the daemon holding this session was from an older cs4ai version and exited on upgrade",
                "edits-safe: every edit was written to disk when you made it — nothing was lost with the daemon",
                "recovery: run `cs4ai session <solution>` for a fresh token, then re-fire this command with it") + "\n");

        return sessionResult;
    }

    /// <summary>stop-daemon: connect-only (never spawn a daemon just to stop it).</summary>
    public static async Task<Cs4AiResult> StopDaemonAsync(
        string slnxPath, string[] args, CancellationToken ct = default)
    {
        var key = DaemonProtocol.PipeKeyFor(slnxPath);
        var pipe = await TryConnectAsync(key, ConnectProbe, ct);
        if (pipe is null)
            return Cs4AiResult.Ok($"no daemon running for '{slnxPath}' — nothing to stop.\n");
        await using (pipe)
            return await SendAsync(pipe, args, null, ct);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Transport
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task<NamedPipeClientStream?> TryConnectAsync(
        string pipeKey, TimeSpan window, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + window;
        var name = DaemonProtocol.PipeNameFor(pipeKey);
        while (true)
        {
            var pipe = new NamedPipeClientStream(
                ".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(250, ct);
                return pipe;
            }
            catch (Exception) when (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await pipe.DisposeAsync();
                await Task.Delay(100, ct);
            }
            catch
            {
                await pipe.DisposeAsync();
                return null;
            }
        }
    }

    private static async Task<Cs4AiResult> SendAsync(
        NamedPipeClientStream pipe, string[] args, string? stdinText, CancellationToken ct)
    {
        var request = new DaemonProtocol.Request(args, stdinText, Cs4AiEngine.Version);
        var bytes = Encoding.UTF8.GetBytes(DaemonProtocol.Serialize(request) + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);

        // A GENEROUS response timeout (not "no timeout"): legitimate slow ops — a fresh daemon's cold
        // MSBuildWorkspace load, a full `verify` build+test — take seconds-to-minutes, so the ceiling
        // sits far above them. Its only job is to convert a TRUE deadlock (a wedged daemon) into an
        // actionable error instead of an infinite hang.
        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        string? line;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ResponseTimeout);
            line = await reader.ReadLineAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Cs4AiResult.FileError(
                $"the daemon did not respond within {ResponseTimeout.TotalMinutes:0} min — it may be " +
                "stuck. Run 'cs4ai stop-daemon <slnx>' and retry.");
        }
        if (line is null)
            return Cs4AiResult.FileError("daemon closed the connection without responding.");

        var response = DaemonProtocol.Deserialize<DaemonProtocol.Response>(line);
        return response?.ToResult()
            ?? Cs4AiResult.FileError("daemon returned a malformed response.");
    }

    private static void SpawnDaemon(string slnxPath, bool debug)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot determine own executable path");

        var workDir = Path.GetDirectoryName(Path.GetFullPath(slnxPath)) ?? ".";
        var argv = new List<string> { "--daemon", Path.GetFullPath(slnxPath) };
        if (debug) argv.Add("--debug");

        // The daemon must inherit NOTHING of this client — it outlives it, and any inherited
        // handle it holds (under a capturing shell, the capture pipe's write end) keeps the
        // caller waiting forever for an EOF that never comes. Managed Process.Start cannot
        // express bInheritHandles=false, so Windows spawns natively (see NativeSpawn).
        if (OperatingSystem.IsWindows() && NativeSpawn.DetachedNoInherit(exe, argv, workDir))
            return;

        // Non-Windows (or native-spawn failure): managed spawn with the daemon's own stdio
        // redirected-then-closed. On Unix non-std fds are close-on-exec, so replacing the std
        // fds is sufficient there; the daemon re-points Console at DaemonLog before writing.
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi);
        if (proc is not null)
        {
            try
            {
                proc.StandardInput.Close();
                proc.StandardOutput.Close();
                proc.StandardError.Close();
            }
            catch
            {
                // Daemon may have exited already (lost spawn race) — nothing to close.
            }
        }
        // Fire and forget: the spawn race (two clients spawning two daemons) is resolved by
        // FirstPipeInstance on the daemon side — the loser exits quietly, both clients connect
        // to the winner.
    }
}
