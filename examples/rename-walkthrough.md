# A complete cs4ai session — rename a method, watch everything cs4ai says back

This is a **real captured transcript** (the `--log` file of an actual session, cs4ai **0.2.56**),
not hand-written documentation. The only edits: the temp directory is shortened to
`C:\demo\walkthrough` and the `--from` paths to their file names, for readability. Regenerating it
means re-running these eight commands against an empty folder.

The task: build a tiny two-type project, then rename `Money.Add` to `Plus` — and see what an agent
gets told at every step.

---

### 1 · Open the session

One command: spawns the daemon if needed, creates the solution (the folder was empty), runs a full
`dotnet build`, and returns the token that routes every later call. The `[build …]` block is the
floor — where you stand before touching anything.

```
#1 exit code: 0
cs4ai session C:\demo\walkthrough\walkthrough.slnx --log

session created · C:\demo\walkthrough\walkthrough.slnx · sess_01b12d7aff20.d06bd65d296e3b4f
log: cs4ai_sess_01b12d7aff20.d06bd65d296e3b4f.log
[build passed · 0 new errors · 0 new warnings · 0 preexisting]
[build passed]
```

### 2 · Create a project

Structure ops answer in the same grammar as edits: the solution file echoes as a `+`/`-` line
delta, the affected csproj echoes in full (the write returns the read), and the build truth rides
along.

```
#2 exit code: 0
cs4ai create-project sess_01b12d7aff20.d06bd65d296e3b4f Wallet.Core --path src

ok
[create-project walkthrough · walkthrough.slnx · 3 lines]
+   <Folder Name="/src/">
+     <Project Path="src/Wallet.Core.csproj" />
+   </Folder>
[create-project walkthrough · walkthrough.slnx · 3 lines]
[create-project Wallet.Core · src/Wallet.Core.csproj · 9 lines]
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
[create-project Wallet.Core · src/Wallet.Core.csproj · 9 lines]
[build passed · 0 new errors · 0 new warnings · 0 preexisting]
[build passed]
```

### 3–4 · Create two types

Multi-line bodies come from a file (`--from`) — shell-safe. The project and namespace both fall
out of the FQN; no `--kind`, the declaration speaks for itself. The frame carries the file cs4ai
chose, the canonical line count, and the type's **staleness token** (`type_…`) — the thing a later
edit must cite. Note the member order in the response: the write canonicalized the file
(members sorted per the repo's `.cs4aiconfig` preset), and what you get back is what's on disk.

```
#3 exit code: 0
cs4ai create sess_01b12d7aff20.d06bd65d296e3b4f Wallet.Core.Money --from _money.txt

ok
[create Wallet.Core.Money · src/Money.cs · 15 lines · type_f3c04527df59]

public readonly record struct Money
{
    private Money(decimal amount) { Amount = amount; }

    public decimal Amount { get; }

    // Add refuses to be negative; see the Wallet tests before changing it.
    public Money Add(Money other) => new(Amount + other.Amount);

    public static Money From(decimal amount) => new(decimal.Round(amount, 2));

    /// <summary>Adds two amounts. Named-method equivalent: <see cref="Add(Money)"/>.</summary>
    public static Money operator +(Money left, Money right) => left.Add(right);
}
[create Wallet.Core.Money · src/Money.cs · 15 lines · type_f3c04527df59]
[build passed · 0 new errors · 0 new warnings · 0 preexisting]
[build passed]

#4 exit code: 0
cs4ai create sess_01b12d7aff20.d06bd65d296e3b4f Wallet.Core.Wallet --from _wallet.txt

ok
[create Wallet.Core.Wallet · src/Wallet.cs · 10 lines · type_0b6fd680c90e]

public class Wallet
{
    private Money _balance = Money.From(0m);

    public decimal Balance => _balance.Amount;

    // Deposit is a thin wrapper: Add does the arithmetic and keeps the rounding rules.
    public void Deposit(Money amount) => _balance = _balance.Add(amount);
}
[create Wallet.Core.Wallet · src/Wallet.cs · 10 lines · type_0b6fd680c90e]
[build passed · 0 new errors · 0 new warnings · 0 preexisting]
[build passed]
```

### 5 · Read before writing

`inspect` returns one type, whole, with its current token. The token is how cs4ai keeps the agent
honest: every edit must cite the token from a read, and a stale citation is refused with the
current shape — the rejection *is* the re-read.

```
#5 exit code: 0
cs4ai inspect sess_01b12d7aff20.d06bd65d296e3b4f Wallet.Core.Money

[inspect Wallet.Core.Money · src/Money.cs · 15 lines · type_f3c04527df59]

public readonly record struct Money
{
    private Money(decimal amount) { Amount = amount; }

    public decimal Amount { get; }

    // Add refuses to be negative; see the Wallet tests before changing it.
    public Money Add(Money other) => new(Amount + other.Amount);

    public static Money From(decimal amount) => new(decimal.Round(amount, 2));

    /// <summary>Adds two amounts. Named-method equivalent: <see cref="Add(Money)"/>.</summary>
    public static Money operator +(Money left, Money right) => left.Add(right);
}
[inspect Wallet.Core.Money · src/Money.cs · 15 lines · type_f3c04527df59]
```

### 6 · Preview the blast radius

`discover` is effectively the rename preview: who the symbol is, and every place that references
it — counted in the units that matter (`refs` = occurrences, the set a rename rewrites).

```
#6 exit code: 0
cs4ai discover sess_01b12d7aff20.d06bd65d296e3b4f Add

# discover 'Add'
Types: [0]
Methods: [1]
  Wallet.Core.Money Wallet.Core.Money.Add(Wallet.Core.Money)
Other: [0]

Referenced by: [3 refs]
  src/Money.cs:14
  src/Money.cs:15
  src/Wallet.cs:10
References: [0 calls]
  (none)
```

### 7 · The rename

One command. Every **bound** reference rewrites — the call in `Wallet.Deposit`, the operator's
delegation, and the doc-comment `cref` (which is why the `///` line is *not* in the stale-comment
report below: it's already correct). What *can't* be rewritten safely — plain-prose comments that
still say `Add` — is reported instead, as whole comment blocks with line extents, ready for a text
edit. The frame carries the **fresh token** (the old one died with the edit), and the `[build]`
block confirms nothing broke.

```
#7 exit code: 0
cs4ai rename sess_01b12d7aff20.d06bd65d296e3b4f Wallet.Core.Money.Add(Money) Plus --token type_f3c04527df59

ok
[rename Wallet.Core.Money · src/Money.cs · 1 lines · type_542e2a0ee646]
Wallet.Core.Money.Add -> Plus
[rename Wallet.Core.Money · src/Money.cs · 1 lines · type_542e2a0ee646]
references-rewritten: 1 ref across 1 other file · src/Wallet.cs
comments-mention-old-name: src/Money.cs:9 · src/Wallet.cs:9 — prose still says 'Add'
[build passed · 0 new errors · 0 new warnings · 0 preexisting]
[build passed]
```

### 8 · The authoritative close

Edits already wrote through to disk (undo is git's job — there is no commit). `verify` is the
end-of-work truth: a real `dotnet build` + `dotnet test`, the full picture a fast Roslyn check
can't see. It also flushes this very transcript.

```
#8 exit code: 0
cs4ai verify sess_01b12d7aff20.d06bd65d296e3b4f

verify
tests: no tests ran
[build passed · 0 new errors · 0 new warnings · 0 preexisting]
[build passed]
```

---

### What didn't happen

Eight commands, and the agent never read a file, never grepped, never guessed a line number, and
never held more source in its context than the two types it created. Every response answered the
next question before it was asked: the token to cite, the files that changed, the comments that
went stale, and whether the build still passes.

Exit codes stayed `0` throughout because they answer only "was the command valid?" — build and
test truth lives in the body, where it can say something more useful than a number.
