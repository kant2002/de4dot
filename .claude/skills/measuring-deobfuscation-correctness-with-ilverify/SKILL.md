---
name: measuring-deobfuscation-correctness-with-ilverify
description: The correctness methodology for evaluating any de4dot change — de4dot-introduced invalid IL via ilverify, filtered for SDK/runtime version noise, is the only trustworthy signal. Use before and after any change to de4dot.blocks or de4dot.code, especially anything touching cflow emulation, control-flow rewriting, or type restoration.
---

# Measuring Deobfuscation Correctness with ilverify

## When to use

- Before/after any change to control-flow deobfuscation, string/constant decryption, type
  restoration, or anything in the shared cflow emulator — i.e. almost any nontrivial change to
  `de4dot.blocks` or `de4dot.code/deobfuscators/`.
- Deciding whether a metric improvement (fewer unresolved dispatches, fewer empty methods, cleaner
  decompile) actually represents a correctness improvement or just looks better superficially.
- Reviewing someone else's (or your own past) change for regressions before building further on it.

## The headline metric: de4dot-introduced invalid IL

Run `ilverify` against the deobfuscated output and count **only** the failures that are (a) not
present in the original obfuscated binary and (b) involve a type internal to the target assembly
(not a framework/third-party type). That count — call it the introduced-bug count — is the ground
truth. Everything else is a heuristic.

Two corrections are required to get an honest number, and both are easy to skip by accident:

1. **Resolve every reference.** If any dependency (runtime or third-party) is missing from the
   reference set passed to `ilverify`, it silently *skips* methods it can't fully resolve instead of
   failing them — so an incomplete reference set produces a massive **under-count**, not a
   conservative one. Assemble a complete, version-matched reference set before trusting any number.
2. **Filter version-mismatch noise.** If the SDK/runtime available locally is *newer* than what the
   target assembly was originally built against, `ilverify` reports large numbers of false positives
   because API signatures differ across versions — and de4dot's *correct* removal of proxy
   indirection makes previously-hidden framework calls newly visible to the verifier, inflating the
   count further. Compare deobfuscated output against the **original obfuscated binary** and count
   only newly-failing methods that touch a plugin/target-internal type; that's the version-independent
   signal. Raw absolute `ilverify` error counts on a modern SDK against an older target are typically
   ~99% version noise — never report or act on the raw number alone.

## Workflow

1. Build the change.
2. Run the deobfuscation pipeline against your test corpus.
3. Run `ilverify` against both the deobfuscated output and the original obfuscated input, with an
   identical, complete reference set for both.
4. Diff: methods that fail in the deobfuscated output but not the original, filtered to
   target-internal types only, are the introduced-bug count for this change.
5. Compare against the last known-good baseline for that count. **A change may not raise it.**
   Secondary gates that must also hold: no new empty method bodies (a rewrite that "succeeds" by
   deleting live code is not a fix) and no new stack underflows.
6. Track readability signals (unresolved-dispatch count, infinite-loop-shaped decompiles, leftover
   `goto` density) as a secondary, *non-authoritative* trendline — never as a gate by themselves.

## Why the secondary signals are gameable

A control-flow rewrite pass can be marked "fully resolved" internally (e.g. a zero-failure count on
its own bookkeeping) while a passthrough block was never actually rewired — the method still runs,
but silently wrong, or the pass instead deletes the now-"unreachable"-looking dead code outright.
Either way, unresolved-dispatch/goto counts drop and look like progress while the introduced-bug
count would show the truth. Concretely: a change that makes "unresolved dispatches" or "empty
methods" trend in the *same direction* without the introduced-bug count improving (or that improves
empty-method count while the introduced-bug count does not) is not a fix — investigate before
merging.

## Common scenarios

**Scenario: a candidate fix drops the unresolved-dispatch count sharply but you haven't checked IL
validity yet.** Don't trust it yet. Run the full ilverify diff first — a sharp readability
improvement achieved by deleting or mis-rewiring control-flow edges is exactly the failure mode
described above, and has happened before on this codebase (see
the `debugging-xorswitch-control-flow-recovery` skill for a concrete instance).

**Scenario: you're evaluating a change to something used by every deobfuscator (e.g. the shared
cflow emulator).** Run the corpus-wide comparison, not just against the sample that motivated the
change — a fix for one obfuscator's quirk can regress every other deobfuscator that shares the same
emulator code path. See the `hardening-the-shared-cflow-emulator` skill.

## Pitfalls

- Don't compare `ilverify` counts across two different SDK/runtime versions and draw conclusions —
  re-run both sides under the identical toolchain version.
- Don't accept "readability looks better" as sufficient justification without the introduced-bug
  diff — this codebase has concrete prior cases where that judgment was wrong.
- Don't silently expand the reference set between baseline and candidate runs — an
  inconsistent reference set invalidates the comparison in either direction (masking real bugs or
  inventing fake ones).
