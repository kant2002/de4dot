# de4dot .NET Reactor — Improvement Worklog

One-by-one task tracker. Full context in `IMPROVEMENT_PLAN.md`. Test corpus: three .NET Reactor
v6.x assemblies — **S1**, **S2** (smaller), **S3** (large); not in this repo.

## Correctness metric

`realBug` = de4dot-**introduced** invalid-IL methods (via `ilverify`) that involve a **plugin-internal
type**, i.e. genuine de4dot bugs after filtering SDK/runtime version-mismatch false positives. See
IMPROVEMENT_PLAN.md → "Correctness methodology". **Honest baseline: realBug = 6/6/0 (S1/S2/S3); never
raise it.** de4dot *fixes* ~396/397/100 more than it introduces. Raw absolute ilverify counts are
~99% version noise — never use them.

Also gate on `emptyM` (empty method bodies = deleted live code) and stack underflows: both must stay
at baseline. `dispatch`/`infLoop`/goto are readability signals and are DELETION-GAMEABLE — a drop can
mean deleted code, so never trust them without checking `realBug`.

Every fix: build → scorecard → confirm `realBug`/`emptyM` did not rise and no underflows appear.

## Queue

- [x] **1. Shift-guard emulator regression** — restored out-of-range guard in `Int32Value`/`Int64Value`
  Shl/Shr/Shr_Un (unknown operand was becoming a known 0). Shared fix; helps every emulating
  deobfuscator. DONE.
- [x] **2. Ctor box-drop / TypesRestorer field mistyping** — `TypesRestorer` narrowed a genuinely-`object`
  field to `string` off the one write it could type (boxed value-type writes are un-typeable and were
  ignored). Added `hasUnknownWrite`: never narrow a field/arg with an un-typeable write. General fix;
  largest `realBug` reduction. DONE.
- [x] **3. DisplayClassCleaner hardening** — `PruneReferencedRemovals` (fixpoint reference check: never
  remove a member still referenced by remaining code) + tightened the null-check-guard pattern to a
  pure-guard body. Defensive (findings were latent on this corpus); no regression. DONE.
- [ ] **4. Reflection-proxy type confusion — the ONLY remaining real IL-bug class (realBug 6/6/0).**
  6 methods in each smaller sample, 0 in the large one. All `StackUnexpected`.
  CONFIRMED TRANSFORM (decompiled diff): de4dot rewrites
  `return Wrapper.Proxy(this, name, flags, binder, types, mods, delegate)`
  → `return ((Type)this).GetMethod(name, flags, binder, types, mods)` — drops the delegate arg and
  uses `this` (the wrapper, NOT a `System.Type`) as the receiver → unverifiable IL. The wrapper types
  are `static`/`sealed` and NOT `System.Type` (same in original and deobf — hierarchy not broken).
  RULED OUT: the `inlineCandidate` inliner in `ObfuscatedFile.cs` — instrumented its rewrite loop; it
  does NOT touch these methods. A naive `IsInlineTypeSafe` guard on that inliner made it WORSE (6→8,
  large sample 0→2) by blocking other legit proxy inlining — reverted.
  NEXT: instrument `ProxyCallFixer` (v4; resolves Reactor delegate proxies via a token dict — the
  trailing delegate arg fits) and the cflow method-call inliner to catch the `Proxy → GetMethod`
  rewrite, then guard it: skip when the receiver's static type isn't assignable to the target's
  declaring type (leaving the valid original call). Do NOT guess the pass again — identify it by
  instrumentation first. The large sample is unaffected; de4dot is otherwise IL-correct.
- [ ] **5. Two-variable chained dispatch (Exp 4)** — DEFERRED. Needs joint inner+outer resolution +
  explicit stack rebalancing + per-method re-verification gating. Three prior attempts all produced
  invalid IL (see IMPROVEMENT_PLAN.md → "Two-variable chained dispatch").
- [ ] **6. Open review findings** — FindSimplePath ambiguity + double-compute; duplicated result-tail;
  stale doc. Quality/defensive.

## Notes

- Pre-branch de4dot cannot process these v6.x samples, so the Reactor-path introduced bugs are
  new-feature imperfections, not regressions. Only confirmed shared regression was the shift guard (#1).
- `IDeobfuscator` gained a member = breaking change for out-of-tree plugins (noted, low priority).
