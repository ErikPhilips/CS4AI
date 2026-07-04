using System.Text;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// The daemon's stdio owner + opt-in trace log. <see cref="DaemonClient"/> spawns the daemon with
/// redirected-then-closed pipes (inheriting the client's handles made every output-capturing
/// caller wait forever for an EOF the daemon held open — the "daemon hangs sometimes" bug), so
/// Console.Out/Error MUST be re-pointed away from those dead pipes in every mode. With
/// <c>--debug</c> they point at <c>cs4ai-daemon.log</c> next to the solution — timestamped,
/// pid-tagged, flushed per line, so a wedged or dying daemon leaves evidence; without it they
/// point at <see cref="TextWriter.Null"/> and <see cref="Log"/> is a no-op.
/// </summary>
internal sealed class DaemonLog : IDisposable
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();

    private DaemonLog(TextWriter writer) => _writer = writer;

    /// <summary>Take over Console.Out/Error (log file when <paramref name="debug"/>, else null)
    /// and hook unhandled-exception reporting. An unopenable log file degrades to the null
    /// writer — logging must never be the reason the daemon fails to start.</summary>
    public static DaemonLog Create(string slnxPath, bool debug)
    {
        TextWriter writer = TextWriter.Null;
        if (debug)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(slnxPath)) ?? ".";
                var stream = new FileStream(
                    Path.Combine(dir, "cs4ai-daemon.log"),
                    FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch
            {
                writer = TextWriter.Null;
            }
        }

        var log = new DaemonLog(writer);
        Console.SetOut(writer);
        Console.SetError(writer);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Log($"UNHANDLED EXCEPTION: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            log.Log($"UNOBSERVED TASK EXCEPTION: {e.Exception}");
        return log;
    }

    public void Log(string message)
    {
        if (ReferenceEquals(_writer, TextWriter.Null)) return;
        lock (_gate)
        {
            try
            {
                _writer.WriteLine(
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}");
            }
            catch
            {
                // A failed log write must never take the daemon down.
            }
        }
    }

    /// <summary>One log line per served command: the reconstructed command line, truncated —
    /// a --set-body payload can be an entire file and belongs in the session --log, not here.</summary>
    public static string RenderArgs(string[] args)
    {
        var line = SolutionHost.RenderCommandLine(args);
        return line.Length <= 200 ? line : line[..200] + $"… ({line.Length} chars)";
    }

    public void Dispose()
    {
        // Console still references the writer; flush but leave it open for the dying process.
        lock (_gate)
        {
            try { _writer.Flush(); } catch { /* stream already gone */ }
        }
    }
}
