---
name: cs4ai
description: Semantic C# editor (CLI). Use INSTEAD OF Grep/Read/Edit for any C# symbol question or change — "where is X defined", "who calls X", "find/show me the class or method Y", read or edit a member by name, rename, change signatures, move members, resolve stack traces against source. Not for bulk text substitution or non-.cs files.
---
# cs4ai (CLI)

Shell is bash. Quote all paths (`"C:\repos\Foo\Foo.slnx"`).

A session is a WORK-SPAN, not a transaction: edits write to disk immediately. There is no
commit/discard — undo is git's job (`git checkout`/`restore`). Run `verify` when done.

Every command leads with a session token. Get one first:
- `session ["<…>/Foo.slnx"]` → runs a full build and returns `sess_…`, `solution: opened|created`,
  the full solution `path`, and the opening `buildOutcome` (where you stand before any edit).
  Lead with that token on every later call. The path is optional (defaults to cwd); an absent
  solution is created — as `.slnx` by default (`--format sln` for classic; an explicit
  extension in the path wins).
- add `--log` to record the session's commands (with exit codes) to `cs4ai_<token>.log` on `verify`.
- add `--debug` to a daemon-spawning command (usually `session`) to trace the daemon's
  lifecycle to `cs4ai-daemon.log` next to the solution — for diagnosing daemon behavior only.

Addresses: FQN in C# spelling — `IRepository<T>`, `Foo.Foo(string)`, `Foo.this[int]`,
`Baz(int,string)`. A `file.cs:42` (from a stack trace / build error) also resolves.

Reads:
- `inspect <sess> <addr>` — one type, whole, with a `token: type_…`. Eats a raw stack frame
  (`inspect <sess> --from trace.txt`). Cite that token to edit the type.
- `discover <sess> <name>` — census + who references it (incoming) / what it calls (outgoing).

Edits (write through to disk immediately). Each cites `--token <type_…>` from an `inspect` of
the target type (also echoed in every edit response):
- `create <sess> <new-fqn> --set-body <decl> [--path <dir>] [--in-file <file>]` — new member
  (cite the type's token) or new top-level type (no token). Kind comes from the body; namespace
  from the FQN. `--in-file` places the type in that file — co-locates into an existing file
  (namespace must match) or names a new one.
- `update <sess> <member> --token <type_…> --set-body <decl>` — replace; cascades if the
  signature changed. Facets: `--set-comment`, `--set-namespace`, `--set-usings`,
  `--set-attributes '[A],[B]'` (whole-replace), `--set-file <path>` (move a TYPE to a file).
- `rename <sess> <addr> <new-name> --token <type_…> [--set-file "New.cs"]` · `move <sess>
  <member> <target> --token …` · `delete <sess> <addr> --token <type_…>`. rename/move/delete
  rewrite call sites. `--set-file` puts a type in a file (intra-project): a new file (git mv
  when the type was alone in its old file, else extracts it) or co-locates into an existing
  file (namespace must match). The frame says which; folding it into `rename` is the usual fix
  for a renamed type whose file name is now stale.
- Any multi-line body → write a file and use `--from <file>` (shell-safe — inline multi-line
  `--set-body` risks a quoting hang). Inline `--set-body` is for one-liners; `--stdin` reads stdin.

Graph / structure (immediate; writes disk + full build + reload):
- `create-project <sess> <name> --path <dir> [--template console]`
- `add-reference <sess> <proj.csproj> <package|ref.csproj> [--version <v>]`
- also: update-project / delete-project / delete-reference.

One result format everywhere (no JSON): a status line, then `[op address · path · N lines · token]`
frames (type source; structure ops echo the affected csproj in full and the solution file as
a `+`/`-` line delta), then a trailing `[build …]` block.
Structure output reads exactly like an edit — learn it once. The exit code answers ONLY "was the
command valid?" (0 = valid; 1 bad-args, 2 inputs-unreadable, 3 ambiguous, 4 not-found, 5 stale,
6 no-config, 7 no-session). Build/Roslyn errors live in the body, never the exit code.

Build axis: every command carries a `[build …]` block. Truth source by command class:
- code edits → Roslyn, **CS* only** (silent on NU*/MSBuild — a code edit can't touch that layer).
- graph edits + `verify` → a real `dotnet build`, **full** picture (CS*+NU*+MSBuild).
  [build failed · 1 new error · 0 new warnings · 2 preexisting]
  + error CS0246 · src/Bar.cs:5 · type 'Baz' not found        (+ = you introduced it)
    warning CS0168 · src/Foo.cs:9 · 'x' declared but never used  ( = was already there)
  [build failed]
- rollup: `passed` | `passed_with_warnings` | `failed`. `failed` = you introduced a NEW error.
- An edit that breaks the build still exits 0. Read the rollup, not the exit code. Fix `+`-tagged
  (new) diagnostics; ` `-tagged ones predate you.

Finish a session:
- `build <sess>` — fast Roslyn CS re-check. `run-test <sess>` — tests on disk.
- `verify <sess>` — the authoritative full build + tests; the final truth (catches NU*/MSBuild a
  code edit can't see). A report, not a gate. Flushes the `--log` transcript if enabled.
  `--raw` appends the verbatim dotnet transcript when the curated [build] block isn't enough.
- A failed build does NOT undo your edit — fix the code (edits are already on disk). To roll
  back, use git.

Recovery (the body carries what the retry needs):
- exit 5 stale/missing `--token` → the response IS the read: current shape + fresh `type_`
  token. Re-cite it.
- exit 6 needs config → ask the user for a preset (recommend sa1201), `cs4ai init <slnx>
  <preset>`, retry. (A solution created via `session` already has one.)
- exit 7 no session → lead with a valid `sess_` token (`session` mints one).

Exit codes: 0 ok · 1 usage · 2 file/parse · 3 ambiguous · 4 not found · 5 stale token ·
6 needs config · 7 no session.