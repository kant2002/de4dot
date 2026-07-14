# de4dot .NET Reactor v6.x — Findings & Improvement Plan

Improvements to de4dot's .NET Reactor deobfuscator (XorSwitch CFG restoration, generic string/
constant decryption, closure cleanup, and shared-emulator correctness), plus the correctness
methodology used to validate them.

Test corpus: **three .NET Reactor v6.x assemblies** — **S1** and **S2** (smaller, ~16 types each)
and **S3** (large, ~70 types). They are not part of this repo.

---

## Correctness methodology (read this first)

The headline correctness metric is **de4dot-introduced invalid IL**, measured with `ilverify`.

Getting this right required two corrections that are easy to get wrong:

1. **Resolve every reference.** With missing reference assemblies, `ilverify` silently *skips*
   methods it can't fully resolve, so error counts are massive under-counts. A complete, matching
   runtime + third-party SDK reference set is required (assembled locally; not in this repo).
2. **Filter version-mismatch noise.** With a *newer* SDK/runtime than the target assemblies were
   built against, `ilverify` reports hundreds of false positives (API signatures differ across
   versions), and de4dot's *correct* un-proxying of framework calls makes those newly *visible*.
   The honest metric therefore compares the deobfuscated output against the **original** obfuscated
   binary and counts only failing methods that **involve a plugin-internal type** — those are
   version-independent and genuinely de4dot's fault.

**Honest baseline (introduced real IL bugs): 6 / 6 / 0 (S1/S2/S3).** de4dot *fixes* far more than
it introduces (~396/397/100 fewer failing methods than the originals), and the large assembly (S3)
comes out fully IL-correct. Raw absolute counts (hundreds per assembly) are ~99% version noise.

A scorecard drives the loop: build → deobfuscate each sample → decompile (`ilspycmd`, project mode,
full refs) → report `realBug` (the metric above) plus readability signals (`dispatch`, `infLoop`,
`emptyM`, goto). **Rule: no change may raise `realBug`, `emptyM`, or introduce stack underflows.**
The readability signals are deletion-gameable — never trust a drop in them without checking `realBug`.

---

## Completed fixes

### Shared cflow emulator
- **Shift out-of-range guard** (`Int32Value`/`Int64Value` `Shl`/`Shr`/`Shr_Un`). A shift count that
  is a nonzero multiple of 32/64 (C# masks the count, making `wordbits - shift == 0`) computed an
  all-bits-valid mask, turning an **unknown operand into a known constant 0**. This corrupts *every*
  deobfuscator that emulates shifts; obfuscators emit oversized shifts deliberately. Guard restored.

### TypesRestorer (shared)
- **Never narrow on partial write info.** `TypesRestorer` restores `object`-typed fields/args to an
  inferred type from their writes, but silently ignored writes whose value type it couldn't determine
  (e.g. a boxed value type). It narrowed a genuinely-`object` field to the single write it *could*
  type (`string`), breaking every other write → invalid IL. Added a `hasUnknownWrite` flag: a
  field/arg with any un-typeable write is left `object`. General fix — accounted for the largest
  drop in introduced bugs.

### XorSwitch (dotNET_Reactor/v4)
- **Self-loop guard** (`SwitchRewriter`): never redirect a block to itself. A basic block has no
  internal branch, so `payload; br self` is an infinite loop regardless of retained payload; leaving
  the edge unresolved yields a recoverable goto instead.
- **Phase-6 double-apply** (`EdgeResolver`): the forward stateVar trace yields the value *at the
  predecessor's entry*, but `TryResolveEdge` re-emulated the backward chain and applied the
  predecessor chain's affine update a second time → wrong (but in-range) case index → wrong control
  flow. Added `seedIsAtPredecessorEntry` to emulate only the predecessor.

### DisplayClassCleaner (dotNET_Reactor/v4)
- **Reference-checked removals** (`PruneReferencedRemovals`, fixpoint): never remove a field/method
  still referenced by code that remains; a dangling MemberRef is invalid metadata.
- **Tightened null-check-guard match**: the removal candidate's tail must be a pure guard body
  (constant loads, branches, returns only) — no real work.

### Generic string/constant decryption (pre-existing, validated)
- `GenericConstantDecrypter` + `GenericConstantInliner` handle v6.x generic constant decryption
  (`!!0 smethod_N<T>(int32)`): extract `mul`/`xor`, compute `(arg*mul)^xor`, top-2-bit type flag,
  bottom-30-bit offset, read from the data blob. All encrypted string calls resolved.

---

## Regression audit of the Reactor v6 branch

The branch that added the v6 deobfuscator also modified **shared** code. Findings:

- **Confirmed shared regression — FIXED:** the shift-guard removal above (affected all obfuscators).
- **Reactor path is new capability, not a regression:** the pre-branch de4dot cannot process these
  v6.x samples at all, so the 6/6/0 introduced bugs are new-feature imperfections, not regressions.
- **Noted (low priority):** `IDeobfuscator` gained a member — a breaking change for out-of-tree
  plugin DLLs that implement the interface directly (internal code is safe via a virtual default).
- **Latent:** `TrackedArrayValue` is a mutable `Value` (aliasing hazard if an array local survives
  speculative re-emulation).
- **Verified safe:** the dnlib 3.6→4.5 migration (incl. the CodeVeil resource-API edits), a
  `Resolver.cs` refactor, and the `Sizeof`/`Unbox`/`Rem_Un`/switch-refactor emulator changes.

---

## Two-variable chained dispatch — three experiments, all reverted

Some methods nest an **outer plain-int `switch(state)`** around the **inner affine xor-switch**;
de4dot only recognizes the inner layer. Full failure leaves a visible unresolved dispatch; partial
resolution corrupts control flow into infinite loops. Three attempts to resolve both layers jointly:

- **Exp 1 — plain-header detection.** Recovered the state machine correctly (a representative method
  decoded to its exact linear form) and dropped dispatches/loops sharply, **but** left the bare
  `switch` block with no stack input → 592 stack underflows. Reverted.
- **Exp 2 — all-or-nothing + atomic dead-switch removal.** Metrics looked great **but** empty method
  bodies exploded — it was **deleting live code** (`FailedCount == 0` is not a true "fully resolved"
  signal; passthrough blocks are marked resolved without being rewired). Reverted.
- **Exp 3 — edge rewrites only, no explicit block removal (reachability cleanup).** Held the deletion
  and underflow guards **but** introduced ~184 new invalid-IL errors (`PathStackDepth`, `ReturnVoid`
  with a leftover state constant on the stack) — the plain state-update push isn't cut, so the stack
  is left unbalanced. Reverted.

**Real fix (Exp 4, not attempted):** joint inner+outer resolution with **explicit stack rebalancing
on every CFG edit** (cut the full state-update expression incl. its push; handle conditional/opaque
state updates), **gated per-method by re-verification** so a rewrite is kept only if the method still
verifies. Treat the current `realBug` baseline as the floor. Substantial, dedicated work.

---

## Code review findings (still open, defensive)

- `FindSimplePath` returns the first BFS path and never detects ambiguity (its doc claims it does);
  a wrong stateVar seed can be derived when multiple case→pred paths differ. (`EdgeResolver`)
- `FindSimplePath` is computed twice per `TryEmulateForSeed`; cache it.
- The "pop TOS, validate case index, read stateVar" tail is duplicated across three methods; extract.
- Stale `FindSimplePath` doc ("30 blocks" vs `maxBlocks = 100`).

---

## Open work (priority order)

1. **Reflection-proxy type confusion — the only remaining real IL-bug class (6/6/0).** See WORKLOG.
   de4dot rewrites a reflection proxy `Wrapper.Proxy(this, args…, delegate)` into
   `((Type)this).GetMethod(args…)`, using the non-`Type` wrapper as the receiver → unverifiable IL.
   Confirmed it is **not** the `inlineCandidate` inliner (instrumented). Next: instrument
   `ProxyCallFixer` / the cflow method-call inliner to catch the rewrite, then guard it to skip when
   the receiver's static type isn't assignable to the target's declaring type (leaving the valid
   original call).
2. **Two-variable chained dispatch (Exp 4)** — see above; drives down remaining unresolved dispatches
   and dispatch-related infinite loops. Larger effort; must hold the `realBug`/`emptyM` guards.
3. **Closure/lambda inlining** — nested `<>c__DisplayClass` closures aren't recursively inlined by the
   decompiler; a de4dot IL transform could inline simple single-delegate DisplayClass patterns.
4. **Open review findings** above (defensive/cleanup).

## Success metrics

1. `realBug` → 0 (currently 6/6/0; only the reflection-proxy class remains).
2. Zero stack underflows and no empty-method regressions (guards, already held).
3. Fewer unresolved dispatches / dispatch-induced infinite loops (blocked on Exp 4).
