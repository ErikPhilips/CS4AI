# cs4ai

Semantic editor for C# code, backed by Roslyn. CLI tool that sits between an AI agent and a C#
codebase the way an IDE sits between a human and code — but built for the agent's actual
constraint profile: minimum sufficient context, semantic operations not textual, staleness-checked
writes, and one result grammar to learn.

**Status: release candidate.** The v2 surface is implemented end-to-end and has been sanded by two
adversarial stress gauntlets (every finding fixed; 138 tests green): the transparent auto-daemon
(named pipe per solution, warm Roslyn workspace, zero handle leakage into the caller), the
session-token-first verb surface, write-through edits with per-type staleness tokens, canonical
formatting on every write, build/test truth attached to every result, and the self-distributing
Claude Code skill.

## Install

Not on NuGet.org yet — install from source (one script):

```powershell
git clone https://github.com/ErikPhilips/CS4AI.git
cd CS4AI
./install.ps1    # builds, packs, installs the global `cs4ai` tool, installs the Claude Code skill
```

Requires the .NET 10 SDK (cs4ai loads workspaces through the SDK's MSBuild). Built and tested
on Windows; Linux is untested. To refresh the Claude Code skill later:
`cs4ai --create-skill "%USERPROFILE%\.claude\skills"`.

## The protocol in one block

```bash
cs4ai session Foo.slnx                 # open-or-create; runs a full build; returns sess_…
                                       # (the daemon spawns itself; the token routes every later call)

cs4ai inspect <sess> Wallet            # read: the whole type + its staleness token (type_…)
cs4ai create  <sess> Acme.Wallet.Deposit(int) --token <type_…> --set-body "public void Deposit(int a) { }"
                                       # edits write STRAIGHT TO DISK — undo is git's job
cs4ai verify  <sess>                   # end-of-work truth: full dotnet build + tests
```

A session is a **work-span, not a transaction**: there is no commit or discard. Every edit lands on
disk the moment it succeeds and carries a trailing `[build …]` block, so the agent always knows
whether it just broke the build — and whether a diagnostic is new (`+`) or was already there.

## Quick reference

```bash
cs4ai --help                            # top-level command list; <verb> --help for detail

cs4ai session [<path>] [--format sln|slnx] [--log] [--debug]
                                        # open-or-create (default: cwd; new solutions are .slnx)
cs4ai inspect  <sess> <addr>            # one type, whole, with its type_… token (--depth, --from trace)
cs4ai discover <sess> <name>            # census: who references it, what it calls

cs4ai create <sess> <new-fqn>  --set-body <decl>   # new member (cite the type token) or new type
cs4ai update <sess> <addr>     --token <t> --set-body <decl>   # member OR whole type, in place
                                        # facets: --set-comment · --set-namespace · --set-usings
                                        #         --set-attributes · --set-file (type → file)
cs4ai rename <sess> <addr> <new-name> --token <t>  # semantic rename; call sites rewritten + counted
cs4ai move   <sess> <member> <type>   --token <t>  # relocate a member; cascades
cs4ai delete <sess> <addr>            --token <t>  # remove a symbol; call sites rewritten

cs4ai create-project <sess> <name> --path <dir>    # structure tier: immediate, full build + reload
cs4ai add-reference  <sess> <proj.csproj> <package|ref.csproj>
                                        # also: update-project · delete-project · delete-reference

cs4ai build    <sess>                   # fast Roslyn CS re-check of the live view
cs4ai run-test <sess>                   # dotnet test on disk; failures vs the session-open baseline
cs4ai verify   <sess> [--raw]           # authoritative full build + tests; --raw appends the
                                        # verbatim dotnet transcript

cs4ai init <slnx> <preset>              # first-contact config (sa1201 | microsoft | source)
cs4ai reload      <slnx>                # invalidate the warm workspace after a git operation
cs4ai stop-daemon <slnx>                # release the pipe, drop the session, exit
```

Addresses are C# spelling: `IRepository<T>`, `Wallet.Deposit(int)`, `Repo.Repo(string)` (ctor),
`Repo.this[int]` (indexer), or a raw `file.cs:42` from a stack trace.

Exit codes answer ONLY "was the command valid?": `0` ok · `1` usage · `2` file/parse ·
`3` ambiguous · `4` not found · `5` stale token · `6` needs config · `7` no session.
Build and test failures are **reported in the body** (`[build …]`, `tests: …`), never the exit
code — and 5/6/7 are handshakes whose refusal body carries exactly what's needed to re-fire.

## Build from source

```bash
dotnet build
dotnet test
./install.ps1                 # local install as a dotnet tool + skill refresh
```

## Design

cs4ai is built for the agent's constraint profile rather than a human's: expensive working
memory, no glanceable state, whole intents instead of keystrokes. The load-bearing decisions
that follow from it:

- **Exit codes answer only "was the command valid?"** Build and test truth lives in the result
  body, tagged new-vs-preexisting, so the agent never confuses "my edit didn't land" with "my
  edit landed and broke the build" — those demand opposite recoveries.
- **A session is a work-span, not a transaction.** Edits write through to disk immediately;
  rollback belongs to your VCS; `verify` is a report, not a gate.
- **Per-type staleness tokens keep the agent honest.** Every read returns one; every edit cites
  one; a stale citation is refused with the current shape — the rejection *is* the re-read.
- **One result grammar.** Every verb — edit, structure, read, lifecycle — answers in the same
  framed format, so the agent learns the output shape once.
- **The agent-facing contract ships inside the binary.** `cs4ai --create-skill` emits it, so
  documentation and behavior can't drift apart.
