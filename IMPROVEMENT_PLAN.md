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

**Current: realBug = 0 / 0 / 0 (S1/S2/S3)** — de4dot emits fully verifiable IL for the whole corpus.

> The figure long recorded here as an "honest baseline" of 6/6/0 was itself measured with an
> incomplete reference set, and per correction (1) above that under-counts rather than over-counts.
> Re-measured against a complete set the true pre-fix baseline was **17/17/1**. See `WORKLOG.md`
> items 4 / 4b / 4c for what those actually were.

A scorecard drives the loop: build → deobfuscate each sample → decompile (`ilspycmd`, project mode,
full refs) → report `realBug` (the metric above) plus readability signals (`dispatch`, `infLoop`,
`emptyM`, goto).

**Rule: no change may raise `realBug` or `emptyM`, introduce stack underflows, or leave a method with
no `ret`/`throw`/`rethrow`.** That last gate exists because `ilverify` structurally cannot see it —
an infinite loop is perfectly type-safe IL, so `realBug` read 0 while 21 methods across the corpus
never returned (WORKLOG #4d). Readability signals are deletion-gameable; never trust a drop in them
without checking `realBug`.

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

## Code review findings — ALL ADDRESSED 2026-07-29 (WORKLOG #6)

- ~~`FindSimplePath` returns the first BFS path and never detects ambiguity (its doc claims it
  does)~~ — it now bails out when a second distinct predecessor can reach the target. This is a
  **correctness** fix, not cosmetic: callers replay the path to derive a stateVar seed, so silently
  taking whichever path BFS found first yields a wrong-but-in-range seed and therefore a wrong edge —
  the same silent-wrongness shape as the phase-6 double-apply bug, and equally invisible to
  `ilverify`. It now fails closed (leaves the edge unresolved).
- ~~computed twice per `TryEmulateForSeed`~~ — memoised per (start, target) for the resolver's
  lifetime. Safe because the CFG is not mutated while edges are being resolved.
- ~~duplicated result tail~~ — extracted to `ReadSeedAndCaseIndex` / `ReadCaseIndex`.
- ~~stale doc ("30 blocks" vs `maxBlocks = 100`)~~ — rewritten to describe what the method
  actually does.

Measured cost of the ambiguity guard: **+1 residual `switch` on S1 and S2, 0 on S3**, and ±3 in
short-vs-long branch encoding. All gates unchanged (`realBug` 0/0/0, 0 non-terminating methods,
0 empty bodies, method counts 1019/1019/2859). The large raw IL text diff is offset renumbering —
the opcode histograms differ only in `br.s`/`brfalse.s` encoding buckets.

---

## Open work (priority order)

1. ~~**Reflection-proxy type confusion**~~ — **DONE 2026-07-29**, along with two further IL-bug
   classes it was masking. `realBug` is now **0/0/0**: de4dot emits fully verifiable IL for the whole
   corpus. See WORKLOG items 4 / 4b / 4c for root causes and fixes. Summary of what was wrong:
   - **4** — the receiver type confusion was *pre-existing in the obfuscated input*, not introduced.
     Reactor declares reflection stubs as `instance` methods whose `this` is really an arbitrary
     receiver passed as an `object` argument to a static proxy dispatcher; resolving the dispatcher
     reinterprets that slot as a typed `this`. Fixed by normalising such stubs to `static`.
   - **4b** — `DotNetUtils.GetMethod2` could not resolve calls made through a *generic instantiation*
     of a type in the same module, so `UnusedMethodsFinder` thought those callees were unreferenced
     and deleted live methods, leaving dangling `MemberRef`s.
   - **4c** — `TypesRestorer` narrowed a parameter of a method that is used to build a delegate,
     without updating the delegate's type argument.
   - **Also corrected the measurement itself**: the old "6/6/0" baseline was taken with an incomplete
     reference set, and `ilverify` silently skips methods it cannot resolve. The true pre-fix
     baseline was **17/17/1**.
2. **Two-variable chained dispatch (Exp 4)** — see above; drives down remaining unresolved dispatches
   and dispatch-related infinite loops. Larger effort; must hold the `realBug`/`emptyM` guards.
3. **Closure/lambda inlining** — nested `<>c__DisplayClass` closures aren't recursively inlined by the
   decompiler; a de4dot IL transform could inline simple single-delegate DisplayClass patterns.
4. **Open review findings** above (defensive/cleanup).

## Success metrics

1. ~~`realBug` → 0~~ **ACHIEVED 2026-07-29: 0/0/0.** Measured with a complete reference set; note the
   reference set must resolve *everything*, or `ilverify` silently skips methods and under-reports.
   The remaining job is to hold this at zero — never let a change raise it.
2. Zero stack underflows and no empty-method regressions (guards, already held).
3. Fewer unresolved dispatches / dispatch-induced infinite loops (blocked on Exp 4).
