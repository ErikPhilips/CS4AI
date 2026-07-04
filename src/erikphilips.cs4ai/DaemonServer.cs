using System.IO.Pipes;
using System.Text;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The per-solution daemon: owns a <see cref="SolutionHost"/> (warm Solution, config lock, edit
/// session) and serves CLI requests over a named pipe. One connection per call, requests served
/// sequentially — the host's state is single-threaded by construction.
/// <para>
/// <b>Spawn-race resolution</b> (the design doc's "Daemon vs. One-Shot" section): the pipe itself
/// is the mutex. The pipe is created with <see cref="PipeOptions.FirstPipeInstance"/> *before*
/// any workspace loading; a second daemon racing for the same solution fails pipe creation and
/// exits quietly while its spawner connects to the winner. Clients connect immediately and wait
/// on the response while the first call pays the cold-load cost.
/// </para>
/// <para>
/// <b>Idle timeout:</b> default 1 hour without a request → clean exit (releases the
/// `.cs4aiconfig` lock and drops any staged session, per Settled #21/#40).
/// </para>
/// </summary>
internal sealed class DaemonServer
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromHours(1);

    private readonly string _slnxPath;
    private readonly TimeSpan _idleTimeout;
    private readonly DaemonLog? _log;
    private int _served;

    public DaemonServer(string slnxPath, TimeSpan? idleTimeout = null, DaemonLog? log = null)
    {
        _slnxPath = Path.GetFullPath(slnxPath);
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _log = log;
    }

    /// <summary>Run the serve loop. Returns when stopped (stop-daemon, idle timeout, or lost
    /// spawn race). Never throws for expected shutdown paths.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var key = DaemonProtocol.PipeKeyFor(_slnxPath);
        var pipeName = DaemonProtocol.PipeNameFor(key);

        NamedPipeServerStream? pipe = TryCreatePipe(pipeName);
        if (pipe is null)
        {
            _log?.Log("lost the spawn race — a daemon for this solution already exists; exiting.");
            return 0; // Lost the spawn race — a daemon for this solution already exists. Exit quietly.
        }

        _log?.Log($"listening · pipe {pipeName} · idle timeout {_idleTimeout}");
        await using var host = new SolutionHost(_slnxPath);

        while (!ct.IsCancellationRequested)
        {
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idleCts.CancelAfter(_idleTimeout);
            try
            {
                await pipe.WaitForConnectionAsync(idleCts.Token);
            }
            catch (OperationCanceledException)
            {
                _log?.Log("idle timeout — exiting.");
                break; // Idle timeout (or external cancellation): clean exit.
            }

            bool stopRequested = false;
            try
            {
                stopRequested = await ServeOneAsync(pipe, host, ct);
            }
            catch (Exception ex)
            {
                // A malformed request or broken pipe must not kill the daemon.
                _log?.Log($"serve error (daemon continues): {ex}");
            }
            finally
            {
                try { if (pipe.IsConnected) pipe.Disconnect(); } catch { /* client vanished */ }
            }

            if (stopRequested) break;
        }

        await pipe.DisposeAsync();
        return 0;
    }

    private static NamedPipeServerStream? TryCreatePipe(string pipeName)
    {
        try
        {
            return new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
        }
        catch (IOException)
        {
            return null; // FirstPipeInstance: someone else already owns this solution's pipe.
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Serve a single connected request. Returns true when the request asked the daemon
    /// to stop.</summary>
    private async Task<bool> ServeOneAsync(NamedPipeServerStream pipe, SolutionHost host, CancellationToken ct)
    {
        var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);
        var line = await reader.ReadLineAsync(ct);
        if (line is null) return false;

        var request = DaemonProtocol.Deserialize<DaemonProtocol.Request>(line);
        if (request is null)
        {
            _log?.Log("malformed request — refused.");
            await WriteResponseAsync(pipe, DaemonProtocol.Response.From(
                Cs4AiResult.UsageError("daemon: malformed request")), ct);
            return false;
        }

        bool stop = request.Args.Length > 0 && request.Args[0] == "stop-daemon";

        // Version handshake: a daemon left running across a tool upgrade would silently serve
        // the old binary. Detect the mismatch, exit, and let the client respawn a fresh daemon.
        if (!stop && request.Version is not null && request.Version != Cs4AiEngine.Version)
        {
            _log?.Log($"version mismatch (client {request.Version}, daemon {Cs4AiEngine.Version}) " +
                      "— exiting for respawn.");
            await WriteResponseAsync(pipe, new DaemonProtocol.Response(
                Cs4AiResult.CodeFileOrParse, false, null, DaemonProtocol.VersionMismatchMarker), ct);
            return true; // exit; the client retries against a fresh daemon
        }

        int n = ++_served;
        _log?.Log($"#{n} begin · {DaemonLog.RenderArgs(request.Args)}");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        Cs4AiResult result;
        if (stop)
        {
            result = Cs4AiResult.Ok($"daemon for '{_slnxPath}' stopping.\n");
        }
        else
        {
            var stdin = request.Stdin is null ? null : new StringReader(request.Stdin);
            result = await host.HandleAsync(request.Args, stdin, ct);
        }

        _log?.Log($"#{n} exit {result.ExitCode} · {elapsed.Elapsed.TotalSeconds:F1}s" +
                  (stop ? " · stop-daemon — exiting." : ""));
        await WriteResponseAsync(pipe, DaemonProtocol.Response.From(result), ct);
        return stop;
    }

    private static async Task WriteResponseAsync(
        NamedPipeServerStream pipe, DaemonProtocol.Response response, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(DaemonProtocol.Serialize(response) + "\n");
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
        if (OperatingSystem.IsWindows())
            try { pipe.WaitForPipeDrain(); } catch { /* client may already have read + gone */ }
    }
}
