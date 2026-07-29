# de4dot .NET Reactor — Improvement Worklog

One-by-one task tracker. Full context in `IMPROVEMENT_PLAN.md`. Test corpus: three .NET Reactor
v6.x assemblies — **S1**, **S2** (smaller), **S3** (large); not in this repo.

## Correctness metric

`realBug` = de4dot-**introduced** invalid-IL methods (via `ilverify`) that involve a **plugin-internal
type**, i.e. genuine de4dot bugs after filtering SDK/runtime version-mismatch false positives. See
IMPROVEMENT_PLAN.md → "Correctness methodology". **Current baseline: realBug = 0/0/0 (S1/S2/S3);
never raise it.**

> **Baseline corrected 2026-07-29.** The old "6/6/0" figure was measured with an **incomplete
> reference-assembly set**. `ilverify` *silently skips* any method it cannot fully resolve, so a
> missing dependency under-counts rather than over-counts. Re-measured against a complete, checked-in
> reference set the true pre-fix baseline was **17/17/1**, not 6/6/0 — 9 real errors per small sample
> were being hidden as "expected third-party noise". Task #4 below then took it to **2/2/1**.
> Task #4b took it to **0/0/1**, and #4c to **0/0/0** — de4dot now emits fully verifiable IL
> for the entire corpus.
> Lesson: never treat a `FileLoadErrorGeneric` as benign noise; it means the numbers next to it are
> undercounts. Assemble a complete reference set *first*, then measure.

Also gate on `emptyM` (empty method bodies = deleted live code), stack underflows, and **methods with
no `ret`/`throw`/`rethrow` at all** (item 4d — an infinite loop is type-safe, so ilverify cannot
see it; note `throw` counts as a valid exit, or iterator Reset() stubs read as false positives):
all must stay
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
- [x] **4. Reflection-proxy type confusion — ROOT-CAUSED AND FIXED (realBug 17/17/1 → 2/2/1).** DONE.
  **It was never a rewriting bug — the receiver type confusion is pre-existing in the obfuscated
  input, and de4dot merely *exposed* it.** Reactor emits reflection stubs declared as `instance`
  methods whose `this` slot does not hold an instance of the declaring type at all: it carries an
  arbitrary receiver that obfuscated callers pass as a weakly-typed `object` argument to a *static*
  proxy dispatcher, e.g.
  `ldarg.0; ldarg.1; ldarg.2; ldsfld <delegate>; call static Proxy(object, string, BindingFlags, D)`.
  That verifies fine (the receiver is only ever an `object` parameter). Once `ProxyCallFixer`
  correctly resolves the dispatcher back to the real *instance* target, the same stack shape
  (receiver + N args) is reinterpreted as `call instance Target::M(...)` — and the `object`-typed
  slot silently becomes a typed `this`. Hence mirror-image errors at both the stub and every caller.
  Confirmed by bisection (`--no-cflow-deob` and `--dont-rename` both still reproduce → not cflow, not
  the renamer) and by direct IL diff of the original vs deobfuscated binary.
  FIX: `de4dot.code/deobfuscators/FakeInstanceStubFixer.cs`, run from Reactor v4 `DeobfuscateEnd()`
  *after* ProxyCallFixer. It makes such a stub honest — converts it to `static` with the receiver as
  an explicit leading parameter typed to the target's declaring type. This needs **zero IL edits**:
  static `arg0` occupies the old `this` slot so `ldarg` indices already line up, and call sites push
  the identical values (receiver + N args == N+1 static args) and reference the MethodDef, so the
  call is re-emitted against the new signature automatically.
  GUARDED: only rewrites methods that are *already provably invalid* — requires the declaring type to
  be non-assignable to the target's declaring type, so legitimate forwarding (a subclass calling a
  base method, e.g. `Editor::get_target`) is untouched; ctors, virtuals, and generics are excluded.
  Verified: 997 methods before and after (deletes nothing), zero empty bodies, S3 unaffected (0 stubs
  matched), and the decompiled C# goes from `((Type)this).GetMethod(...)` on a static class to the
  obviously-correct `type_0.GetMethod(def, pol)`.
- [x] **4b. `MissingMethod` dangling refs — FIXED (realBug 2/2/1 → 0/0/1).** DONE.
  Live methods were being deleted as "Inlined method" while still referenced.
  ROOT CAUSE: `DotNetUtils.GetMethod2(ModuleDef, IMethod)` could not resolve a call to a method on a
  **generic instantiation of a type in this module**. Such a call is a `MemberRef` whose
  `DeclaringType` is a `TypeSpec` (e.g. `C`1<!T>::M(...)`). The scope type resolves to the correct
  `TypeDef` and `FindMethod(name)` finds the method — but `FindMethod(name, MethodSig)` does **not**
  match that MemberRef's signature, so the lookup returned null. Confirmed by instrumentation:
  `scopeIsTypeDef=True sameModule=True findByName=True findBySig=False`.
  `UnusedMethodsFinder` uses that lookup to decide whether a candidate is still referenced, so every
  such call site was invisible → the callee looked unused → deleted → dangling `MemberRef`. de4dot
  even logged its own `ERROR: Could not resolve MethodRef ... (0A...)` at write time; those were
  being ignored.
  FIX: `GetMethod2` now asks dnlib to resolve the reference (`ResolveMethodDef()`, also unwrapping
  `MethodSpec`) before falling back to the scope-type + signature lookup. Shared utility, so it
  helps every deobfuscator that reasons about whether a method is still called.
  Verified: 22 previously-deleted-but-referenced methods now retained on S2, zero empty bodies, type
  count unchanged, and de4dot's own dangling-MethodRef errors drop to 0 on all three samples.
- [x] **4c. `DelegateCtor` on a delegate-pinned method — FIXED (realBug 0/0/1 → 0/0/0).** DONE.
  `TypesRestorer` narrowed a method parameter from `object` to its real type while that method was
  being used to construct a delegate whose type argument still said `object`, so the delegate ctor
  no longer verified. Concretely: `ShowAsContextPost(object)` → `ShowAsContextPost(GenericMenu)`,
  but the wrapper stayed `Action`1<object>` (built via `ldftn` + `newobj`), and the enclosing
  `NewReg<object>(Action<!!0>)` instantiation was not updated either. The narrowing is *correct in
  isolation* — the bug is that the delegate side is not rewritten with it.
  Identified by bisection: `--dr4-types false` → 0 errors, `--dr4-types true` → 1.
  FIX: `TypesRestorer.FindDelegateBoundMethods()` collects every `ldftn`/`ldvirtftn` target up front,
  and `DeobfuscateMethods()` skips signature updates for those methods. Leaving them alone keeps
  them consistent with the delegate, which is the pre-restore state and always valid.
  COST IS NEAR-ZERO, measured not assumed: S3 has 671 distinct delegate-pinned methods, but
  TypesRestorer was only actually narrowing **one** of them — the whole-assembly IL diff is **4
  lines**, all in `ShowAsContextPost`. Method count, type count and empty-body count all unchanged.
  POSSIBLE FUTURE IMPROVEMENT (not needed for correctness): instead of skipping, rewrite the
  delegate's generic argument and the enclosing generic-method instantiation to match the narrowed
  signature. That would recover the nicer `GenericMenu` type, but it means rewriting a TypeSpec and
  a MethodSpec chain — much riskier for one method's readability.
- [x] **4d. Passes could emit methods that never terminate (infinite loop). FIXED: 21 → 0.** DONE.
  Some methods decompiled to `while (true) { ... }` with no exit at all. **Invisible to ilverify** —
  an infinite loop is type-safe, so `realBug` stayed 0 while the output was plainly wrong. This is
  the one bug class the correctness metric structurally cannot see.
  **ONE ROOT CAUSE, THREE PLACES.** A switch rewrite redirects each predecessor of the switch block
  straight to its resolved target. Redirect the *last live* predecessor and the switch itself becomes
  unreachable — and so does every case that was not resolved, which can be the one holding the
  method's only exit. The blocks still exist at that instant, so an "is there still a `ret`?" check
  passes; the next iteration's dead-block cleanup then removes them.
  The measurement change that cracked it: check whether an exit is **reachable**, not whether one
  still exists.
  FIXED IN:
  1. `XorSwitchDeobfuscator`/`SwitchRewriter` (Reactor-specific) — `WouldOrphanMethodExit` simulates
     the pending redirects on a copy of the successor map and skips the dispatch if no `ret`/`throw`
     would stay reachable. Corpus 21 → 19.
  2. `SwitchCflowDeobfuscator` (de4dot's **shared** switch pass) — same defect, found by
     instrumenting `BlocksCflowDeobfuscator`'s loop after every step and every `IBlocksDeobfuscator`
     by name. `DeobfuscateTOS`/`Ldloc`/`StLdloc` all had it. Corpus 19 → 6.
  3. The combined-effect gap: within one `DeobfuscateTOS` call the `Tos_Ldloc` fallback and the
     direct redirects were validated *separately*, each against a graph missing the other's edits.
     Split all three workers into a **plan** phase (`PlanTOS`/`PlanLdloc`/`PlanStLdloc`, which never
     touch the graph) and a single **apply** phase (`ApplyPlan`), so a call unions its own plan with
     the recursive fallback plans and validates the union once. Corpus 6 → 0.
  Rewrites are modelled as a `SwitchRewrite` value (branch / branch+pop / Bcc), so `Bcc` sources
  correctly keep the edge that does not point at the switch. Plans are deduplicated by source, since
  one block can appear in both its own plan and a recursive one.
  ALL GATES HELD THROUGHOUT (verified corpus-wide, since 2 of the 3 sites are shared code):
  `realBug` 0/0/0, zero empty method bodies, method counts unchanged (S1/S2 1019, S3 2859).
  NOTE ON THE METRIC: a correct exit is `ret` **or** `throw`/`rethrow`. Counting only `ret` reports 4
  false positives on S3 — compiler-generated iterator `IEnumerator.Reset()` stubs whose entire body is
  `newobj NotSupportedException; throw`. Those were never broken.
- [ ] **5. Two-variable chained dispatch (Exp 4)** — DEFERRED. Needs joint inner+outer resolution +
  explicit stack rebalancing + per-method re-verification gating. Three prior attempts all produced
  invalid IL (see IMPROVEMENT_PLAN.md → "Two-variable chained dispatch").
- [x] **6. Open review findings — ALL ADDRESSED.** DONE.
  `FindSimplePath` now detects ambiguity and fails closed; it is memoised per (start, target); the
  duplicated "pop TOS, validate case index, read stateVar" tail is extracted to
  `ReadSeedAndCaseIndex`/`ReadCaseIndex`; the stale doc comment is rewritten.
  The ambiguity item was mislabelled as merely defensive — it is a **correctness** fix of the same
  silent-wrongness family as the phase-6 double-apply bug (a wrong-but-in-range seed produces a wrong
  edge, and `ilverify` cannot see it). Cost measured, not assumed: +1 residual `switch` on S1/S2, 0 on
  S3; every gate unchanged.

## Notes

- Pre-branch de4dot cannot process these v6.x samples, so the Reactor-path introduced bugs are
  new-feature imperfections, not regressions. Only confirmed shared regression was the shift guard (#1).
- `IDeobfuscator` gained a member = breaking change for out-of-tree plugins (noted, low priority).
