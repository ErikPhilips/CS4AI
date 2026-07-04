using System.Text;
using ErikPhilips.Cs4Ai;

// Emit/consume UTF-8 (no BOM) so non-ASCII pipes faithfully on any console.
// Guarded: a detached or redirected handle can throw when the encoding is set.
try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* no console attached */ }
try { Console.InputEncoding  = new UTF8Encoding(false); } catch { /* no console attached */ }

// Thin CLI shell: read argv, hand to the engine, map the result to stdout/stderr + exit code.
// All semantic / Roslyn logic lives in Cs4AiEngine. Matches md4ai's Program.cs pattern.
// stdin is OPT-IN: only hand it to the engine when the caller explicitly passed --stdin. Reading it
// unconditionally (merely because it's redirected) risks a hang if the stream is open without EOF —
// a mis-quoted/heredoc multi-line invocation would block the whole process before it did anything.
var engine = new Cs4AiEngine();
var wantsStdin = Array.IndexOf(args, "--stdin") >= 0;
var result = await engine.ExecuteAsync(args, wantsStdin ? Console.In : null);

if (result.Output is not null)
    Console.Out.Write(result.Output);

if (result.Error is not null)
    Console.Error.WriteLine(
        result.Error.StartsWith("error:", StringComparison.Ordinal)
            ? result.Error
            : $"error: {result.Error}");

return result.ExitCode;
