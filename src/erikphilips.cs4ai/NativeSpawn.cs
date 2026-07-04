using System.Runtime.InteropServices;
using System.Text;

namespace ErikPhilips.Cs4Ai;

/// <summary>
/// Windows-only detached spawn with <c>bInheritHandles=false</c>. .NET's <c>Process.Start</c>
/// always calls CreateProcess with handle inheritance enabled, which duplicates EVERY inheritable
/// handle of this client into the daemon — under a capturing shell that includes the capture
/// pipe's write end, so the daemon holds the pipe open for its one-hour lifetime and the shell
/// waits forever for an EOF that never comes (found live: cold-spawn + <c>$(cs4ai … | grep …)</c>
/// hung while the daemon log showed the request served in seconds; warm daemon or file redirect
/// were fine). Redirecting the child's own stdio (the 0.2.38 attempt) cannot prevent that.
/// <c>bInheritHandles=false</c> + <c>DETACHED_PROCESS</c> hands the daemon nothing of ours.
/// </summary>
internal static class NativeSpawn
{
    private const uint DetachedProcess = 0x00000008;
    private const uint CreateNewProcessGroup = 0x00000200;

    /// <summary>Launch <paramref name="exe"/> detached, inheriting no handles. Returns false on
    /// failure so the caller can fall back to the managed spawn (better a possible hang than no
    /// daemon at all).</summary>
    public static bool DetachedNoInherit(string exe, IReadOnlyList<string> args, string workingDir)
    {
        var cmd = new StringBuilder();
        AppendQuoted(cmd, exe);
        foreach (var a in args)
        {
            cmd.Append(' ');
            AppendQuoted(cmd, a);
        }

        var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
        if (!CreateProcessW(
                exe, cmd, IntPtr.Zero, IntPtr.Zero,
                bInheritHandles: false,
                DetachedProcess | CreateNewProcessGroup,
                IntPtr.Zero, workingDir, ref si, out var pi))
            return false;

        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
        return true;
    }

    /// <summary>Windows command-line quoting (the CommandLineToArgvW inverse): quote when needed,
    /// double the backslash run before an embedded/closing quote.</summary>
    private static void AppendQuoted(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string lpApplicationName,
        StringBuilder lpCommandLine,   // mutable: CreateProcessW may write into the buffer
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
