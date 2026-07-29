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
no `ret`/`throw` at all** (see item 4d — an infinite loop is type-safe, so ilverify cannot see it):
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
- [~] **4d. XorSwitch can emit methods with NO `ret` at all (infinite loop). PARTIALLY FIXED.**
  S2 4 → 2; S1 6, S3 15 unchanged (the guard does not fire there). NOT caught by ilverify — an
  infinite loop is type-safe, so `realBug` stays 0 while the output is plainly wrong.
  **MECHANISM FOUND (one of at least two).** When only *some* of a dispatch's cases resolve, each
  applied edge redirects a predecessor away from the switch block. Redirecting the last live
  predecessor orphans the switch — and with it every case that was *not* resolved, which can be the
  one holding the method's only exit. The blocks still exist at that moment (so a
  "does a ret still exist?" check passes), but de4dot's later dead-block cleanup removes them, and
  the method is left looping forever. Confirmed by instrumenting entry/exit *reachability* per pass:
  `CancelUtils` and `ConnectProcess` both went reachable → unreachable inside a single pass, both
  with `failed=1`.
  FIX: `SwitchRewriter.WouldOrphanMethodExit` simulates the pending redirects on a copy of the
  successor map and, if no `ret`/`throw` would remain reachable from entry, skips the whole dispatch.
  The switch then survives as a recoverable `goto`, which the xorswitch skill already states is
  strictly better than a bogus self-loop. Read-only simulation, so there is nothing to undo.
  Verified: `realBug` still 0/0/0 on all three samples; cost is 4 dispatches left unresolved on S2
  and **zero** on S1/S3.
  REMAINING (2 on S2 — `RunWatcher`, `CustomizeProduct`; plus S1/S3): a *second* mechanism. These
  never trip the reachability check inside XorSwitch — the exit is still reachable when the pass
  returns — yet disabling XorSwitch alone makes them go away, so it reshapes the graph and a later
  pass (`DotNetReactorCflowDeobfuscator`, `MethodCallInliner`, or the generic cflow cleanup) finishes
  the job. NEXT: apply the same reachability check as a *post-condition* around the other block
  deobfuscators to find which one drops it, rather than guessing.
- [ ] **5. Two-variable chained dispatch (Exp 4)** — DEFERRED. Needs joint inner+outer resolution +
  explicit stack rebalancing + per-method re-verification gating. Three prior attempts all produced
  invalid IL (see IMPROVEMENT_PLAN.md → "Two-variable chained dispatch").
- [ ] **6. Open review findings** — FindSimplePath ambiguity + double-compute; duplicated result-tail;
  stale doc. Quality/defensive.

## Notes

- Pre-branch de4dot cannot process these v6.x samples, so the Reactor-path introduced bugs are
  new-feature imperfections, not regressions. Only confirmed shared regression was the shift guard (#1).
- `IDeobfuscator` gained a member = breaking change for out-of-tree plugins (noted, low priority).
