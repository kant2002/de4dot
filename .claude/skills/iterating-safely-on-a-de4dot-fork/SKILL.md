---
name: iterating-safely-on-a-de4dot-fork
description: The meta-workflow and toolchain gotchas for iterating on this de4dot fork — build commands, the living WORKLOG.md/IMPROVEMENT_PLAN.md convention, de4dot's single-file CLI invocation syntax, ilspycmd's runtime mismatch, and how to A/B test a change when git push-adjacent commands are blocked in this environment. Use at the start of any multi-step improvement effort and whenever running de4dot or ilspycmd from the command line.
---

# Iterating Safely on a de4dot Fork

## When to use

- Starting a nontrivial improvement effort (more than a one-line fix) on this fork.
- Running de4dot or ilspycmd from the command line and unsure of the exact invocation.
- Needing to compare a candidate change against the current HEAD (A/B testing) in an environment
  where `git push`-adjacent commands are blocked.

## Build commands

Two solution files, one per target framework:

```bash
dotnet build -c Release de4dot.netcore.sln                      # .NET 8, primary dev target
dotnet build -c Release -f net48 de4dot.netframework.sln         # .NET Framework 4.8
pwsh build.ps1                                                    # full release build, both targets
pwsh test.ps1                                                     # IL-based inlining tests (needs ilasm/ildasm)
```

There is no xUnit/NUnit/MSTest project — the only automated tests are the IL-based integration tests
under `tests/samples/inlining/` (assemble IL → run de4dot → disassemble → compare).

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

## The living-document convention: WORKLOG.md + IMPROVEMENT_PLAN.md

This fork tracks its improvement effort in two root-level files, not just commit messages:

- **`IMPROVEMENT_PLAN.md`** — the full findings/methodology/experiment history. Includes completed
  fixes with their root-cause explanation, a regression audit, failed-experiment writeups (so a
  failed approach isn't silently retried), open code-review findings, and a priority-ordered open
  work list.
- **`WORKLOG.md`** — a terser one-by-one task queue referencing the plan, with a running correctness
  baseline restated at the top so it's never more than a scroll away.

**Before starting any nontrivial change, read both.** They exist specifically so that a failed
approach (see the `debugging-xorswitch-control-flow-recovery` skill for three such failures) doesn't get
re-attempted from scratch, and so the current correctness baseline
(the `measuring-deobfuscation-correctness-with-ilverify` skill) is always known rather than re-derived.
**After landing any nontrivial change, update both** — mark the worklog item done, and add the
finding (root cause, what was tried, what fixed it, what didn't) to the plan in the same style as
existing entries. An undocumented fix is much less valuable than a documented one here, because the
next session's first move is reading these files, not `git log`.

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

**Scenario: starting work on an item from `WORKLOG.md`'s open queue.** Read the relevant section of
`IMPROVEMENT_PLAN.md` in full first — the open items are ordered by priority for a reason, and the
plan document usually already contains a specific "next step" recommendation (e.g. "instrument X
before guessing the responsible pass") that saves significant rediscovery time.

**Scenario: a fix looks done and tests pass.** Before considering it complete, do the ilverify
correctness diff (the `measuring-deobfuscation-correctness-with-ilverify` skill) and update
`WORKLOG.md`/`IMPROVEMENT_PLAN.md` — "builds and passes the IL inlining tests" is necessary but not
sufficient for a change in this codebase, since the inlining tests don't cover most of the surface
area these efforts touch (control-flow rewriting, generic constant decryption, type restoration).

## Pitfalls

- Don't use `-r` expecting single-file behavior — it's recursive-directory mode and will reject a
  file path outright.
- Don't assume a failed git command is a real repository hook problem — check whether it merely
  contains the substring `push` first.
- Don't skip updating `IMPROVEMENT_PLAN.md`/`WORKLOG.md` because "the commit message covers it" —
  the next session reads these files first, not history.
