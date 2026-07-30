---
name: iterating-safely-on-a-de4dot-fork
description: The meta-workflow and toolchain gotchas for iterating on this de4dot fork — build commands, the ROADMAP.md/WORKLOG.md convention, de4dot's single-file CLI invocation syntax, ilspycmd's runtime mismatch, and how to A/B test a change when git push-adjacent commands are blocked in this environment. Use at the start of any multi-step improvement effort and whenever running de4dot or ilspycmd from the command line.
---

# Iterating Safely on a de4dot Fork

## Running the tests

```bash
python3 tests/run_xorswitch_tests.py --fetch-tools   # portable, works on Linux
pwsh test.ps1                                        # Windows only
```

`test.ps1` cannot run outside Windows: hardcoded NETFX tool paths, a `win-x64` de4dot, and a byte
comparison against checked-in `.cleaned.il` that a different ildasm build will never reproduce. The
Python runner covers the XorSwitch dispatch fixtures instead, asserting the resolver's *decision*
(from `DE4DOT_XORSWITCH_TRACE`) plus the resulting payload sequence. `--fetch-tools` restores `ilasm`
from NuGet, because it ships in no SDK and finding that out costs an hour.

Two CLI traps the fixtures encode, both of which produce confusing failures rather than errors:
`-v` is **global** and must precede the filename, while `-p <type>` is **per-file** and must follow
it — mis-ordered, de4dot reports "Missing input file". And these fixtures need `-p dr4`: they carry
no Reactor markers, so detection would score them as something else and the pass under test would
never run at all.

## When to use

- Starting a nontrivial improvement effort (more than a one-line fix) on this fork.
- Running de4dot or ilspycmd from the command line and unsure of the exact invocation.
- Needing to compare a candidate change against the current HEAD (A/B testing) in an environment
  where `git push`-adjacent commands are blocked.

## Build commands

**Canonical list is in `CLAUDE.md` → "Build Commands"; read it there rather than trusting a copy.**
The short version: solution *filters* per target framework (`de4dot.slnx` is the full solution),
`de4dot.net.slnf` for the primary .NET target, `de4dot.netframework.slnf` with `-f net48` for .NET
Framework, `build.ps1` for both, `test.ps1` for the IL inlining tests.

There is no xUnit/NUnit/MSTest project — the only automated tests are the IL-based integration tests
under `tests/samples/inlining/` (assemble IL → run de4dot → disassemble → compare).

> This section used to carry its own copy of the commands and named `de4dot.netcore.sln`, a file that
> does not exist, at ".NET 8, primary dev target" when the projects had already moved to net10.0. That
> is the drift this skill's one-place rule exists to prevent — and it is why the commands now live in
> one file only.

## Running de4dot from the command line

Single-file mode uses `-f <input> -o <output>` — **not** `-r`, which is recursive-**directory** mode
and will reject a plain file path:

```bash
dotnet <path-to-build>/de4dot.dll -f input.dll -o output.dll
dotnet <path-to-build>/de4dot.dll -v -f input.dll -o output.dll   # -v for verbose pass-by-pass output
```

If a sample assembly predates a modern signing algorithm, an older/expired-hash strong-name
signature can trip signature-verification errors in the .NET runtime itself before de4dot even
loads it; setting `OPENSSL_ENABLE_SHA1_SIGNATURES=1` in the environment works around this at the
runtime level.

## Running ilspycmd (for inspecting output)

ilspycmd targets net8 specifically. If only net9/10 SDKs are installed, it fails to launch unless
you force a runtime roll-forward:

```bash
DOTNET_ROLL_FORWARD=LatestMajor ilspycmd -p -o <out-dir> <deobfuscated.dll>
```

## The living-document convention: ROADMAP.md is the record, WORKLOG.md is the queue

This fork tracks its improvement effort in two root-level files, not just commit messages, and the
split between them is strict:

- **`ROADMAP.md`** — the **only** narrative document. Measured state, the correctness metric, every
  root cause found, the gate hierarchy and each gate's blind spot, failed-experiment writeups (so a
  failed approach isn't silently retried), and the priority-ordered path to done.
- **`WORKLOG.md`** — a bare checkbox queue. One line per task: status plus a pointer to the ROADMAP
  section. It deliberately carries no explanation of its own.

**Before starting any nontrivial change, read `ROADMAP.md`.** It exists so that a failed approach
(see the `debugging-xorswitch-control-flow-recovery` skill for several) isn't re-attempted from
scratch, and so the current correctness baseline is known rather than re-derived.

**After landing any nontrivial change: add the finding to `ROADMAP.md`, tick the box in
`WORKLOG.md`.** An undocumented fix is much less valuable than a documented one here, because the next
session's first move is reading these files, not `git log`.

**Write the finding in exactly one place.** These two files replaced three that each restated the
metric, the baseline correction and the same root causes; the copies drifted until they contradicted
each other, and there was no way to tell which was right without re-measuring. If a fact belongs in
ROADMAP, link to its section from anywhere else that needs it — including from these skills. Adding
"just a short summary" in a second file is how that mess regrows.

**This repo must read as fully self-contained — see `CLAUDE.md` → "Documentation rules".** Do not
name or allude to any other repository, organisation or body of work that supplies test material, not
even indirectly. The corpus is `S1`/`S2`/`S3`; external tooling is described by what it measures, never
named; illustrative identifiers are de4dot-generated or invented, never lifted from a target assembly
in a way that would identify it.

## A/B testing a change when git push-adjacent commands are blocked

This environment blocks shell commands containing `git push` — and this has been observed to also
catch `git stash push` (the subcommand name matches the pattern), so stashing is not a reliable way
to flip between "my change" and "HEAD" for a quick comparison. Use file-copy instead:

```bash
cp de4dot.blocks/cflow/Int32Value.cs /tmp/Int32Value.cs.candidate   # save your change aside
git checkout -- de4dot.blocks/cflow/Int32Value.cs                   # restore HEAD version
# build + run scorecard against HEAD
cp /tmp/Int32Value.cs.candidate de4dot.blocks/cflow/Int32Value.cs   # restore your change
# build + run scorecard against candidate
```

`git checkout <ref> -- <path>` and plain `git add`/`git commit` are unaffected — only the
`push`-containing invocations are blocked. If a command unexpectedly reports being blocked by a
"local hook" and you don't see a repo hook that would explain it, assume it's this environment-level
guard rather than something to debug in `.git/hooks/`.

## Common scenarios

**Scenario: starting work on an item from `WORKLOG.md`'s open queue.** Follow its pointer and read
that `ROADMAP.md` section in full first — the open items are ordered by priority for a reason, and the
roadmap usually already contains a specific "next step" recommendation (e.g. "instrument X before
guessing the responsible pass") that saves significant rediscovery time.

**Scenario: a fix looks done and tests pass.** Before considering it complete, do the ilverify
correctness diff (the `measuring-deobfuscation-correctness-with-ilverify` skill) and update
`ROADMAP.md`/`WORKLOG.md` — "builds and passes the IL inlining tests" is necessary but not
sufficient for a change in this codebase, since the inlining tests don't cover most of the surface
area these efforts touch (control-flow rewriting, generic constant decryption, type restoration).

## Pitfalls

- Don't use `-r` expecting single-file behavior — it's recursive-directory mode and will reject a
  file path outright.
- Don't assume a failed git command is a real repository hook problem — check whether it merely
  contains the substring `push` first.
- Don't skip updating `ROADMAP.md`/`WORKLOG.md` because "the commit message covers it" —
  the next session reads these files first, not history.
