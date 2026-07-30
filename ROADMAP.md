# .NET Reactor v6.x — State of the Deobfuscator, and what "fully functional" needs

Goal: a deobfuscator whose output can be trusted as a faithful reconstruction of the original
assembly — not merely one that loads, verifies, and looks tidy.

**This is the only narrative document for the effort.** It holds the measured state, the correctness
metric, every root cause found so far, the gate hierarchy, the failed experiments, and the ordered
path to done. `WORKLOG.md` is a bare task queue that points back into these sections; it deliberately
carries no explanation of its own.

Test corpus: three .NET Reactor v6.x assemblies — **S1**, **S2** (smaller, ~16 types each), **S3**
(large, ~70 types). Not in this repo.

> **Keeping this file compact is part of maintaining it.** Every fact here has exactly one home. If
> you find yourself restating the metric, a root cause, or a failed experiment in a second place,
> link to the section instead — three overlapping documents is the state this file was merged out of,
> and the copies had already drifted into contradicting each other. See "Documentation rules" in
> `CLAUDE.md`.

---

## 1. Where it actually stands (measured, not asserted)

| property | state | how it is measured |
|---|---|---|
| Introduced invalid IL (`realBug`) | **0 / 0 / 0** | `ilverify` vs original, complete reference set, target-internal types only |
| Methods that never terminate | **0** | no `ret`/`throw`/`rethrow` anywhere in the body |
| Empty method bodies (deleted live code) | **0** | `Code size: 0` in the IL dump |
| Stack underflows | **0** | `ilverify` |
| Dangling `MemberRef`s | **0** | de4dot's own "Could not resolve MethodRef" output |
| Semantically broken state machines | **0** (was 19) | state-trace from the seed — see §5, §6 |
| Undecidable dispatches (left unresolved, *faithful*) | 68 | trace hits a non-constant transition |
| Machines traced as terminating | 20 (was 1) | includes all 19 that branch-and-select recovered |
| Decryption coverage (bytes extracted) | 35 / 35 / 27 | gate 7, under the net10.0 host |

The first five rows are the historical definition of "correct". The sixth is why that was never the
same as functional: those methods verified perfectly, terminated structurally, and were wrong. It is
now zero — §5 has the defect, §6 how it is contained.

> The broken/undecidable split was **17 / 8** when the state trace was a set of regexes over
> decompiled C#. Replacing it with a real C#-parser-based analysis reclassified 10 machines from
> "broken" to "undecidable": a parser sees every form of write to the state variable, where the
> regexes missed `num++`, `num <<= 1` and similar and therefore read those cases as "does not touch
> the state" — a false *broken* verdict. Stricter is correct here; a broken verdict has to be
> trustworthy enough to act on.

---

## 2. The correctness metric

Stated once, here. Everything else that needs it links to this section.

`realBug` = the count of de4dot-**introduced** invalid-IL methods, measured with `ilverify`, that
involve a **target-internal type** — one defined in the assembly being deobfuscated.
**Current baseline: 0 / 0 / 0 (S1/S2/S3). Never raise it.**

Getting this number right required two corrections that are easy to get wrong, and both have bitten
this project:

1. **Resolve every reference.** With missing reference assemblies `ilverify` silently *skips* methods
   it cannot fully resolve, so error counts are massive **under**-counts. A complete, matching runtime
   + third-party SDK reference set is required (assembled locally; not in this repo). Treat every
   `FileLoadErrorGeneric` as a defect in your reference set, never as noise — it means the numbers
   printed next to it are undercounts.
2. **Filter version-mismatch noise.** Against a *newer* SDK/runtime than the targets were built for,
   `ilverify` reports hundreds of false positives (API signatures differ across versions), and
   de4dot's *correct* un-proxying of framework calls makes those newly visible. So compare the
   deobfuscated output against the **original obfuscated binary** and count only failing methods
   involving a target-internal type. On a newer SDK, ~99% of raw errors are version noise.

> **The recorded baseline was "6/6/0" for a long time, and it was wrong.** It was measured with an
> incomplete reference set, so per correction (1) it under-counted. Re-measured against a complete
> set, the true pre-fix baseline was **17/17/1** — 9 real errors per small sample were being hidden
> as "expected third-party noise".

**The gating rule for any change.** No change may raise `realBug` or `emptyM` (empty method bodies =
deleted live code), introduce stack underflows, or leave a method with no `ret`/`throw`/`rethrow`.
That last gate exists because `ilverify` structurally cannot see it: an infinite loop is perfectly
type-safe IL, so `realBug` read 0 while 21 methods across the corpus never returned (§3, #4d).

**Readability signals are deletion-gameable.** Unresolved-dispatch counts, `goto` density and
`infLoop` all improve when code is deleted. Never trust a drop in them without checking the gates in
§4 — and read §4's blind-spot note before accepting any change whose evidence is "fewer
instructions, fewer unresolved dispatches".

---

## 3. What was wrong, and what it taught

Eight bug classes found and fixed. The root causes matter more than the fixes, because they rhyme.

**#1 — Shift out-of-range guard (shared cflow emulator).** `Int32Value`/`Int64Value` `Shl`/`Shr`/
`Shr_Un`: a shift count that is a nonzero multiple of 32/64 (C# masks the count, making
`wordbits - shift == 0`) computed an all-bits-valid mask, turning an **unknown operand into a known
constant 0**. This corrupts *every* deobfuscator that emulates shifts, and obfuscators emit oversized
shifts deliberately. Guard restored.

**#2 — `TypesRestorer` narrowing on partial write information.** It restores `object`-typed
fields/args to a type inferred from their writes, but silently ignored writes whose value type it
could not determine (e.g. a boxed value type). So it narrowed a genuinely-`object` field to the one
write it *could* type (`string`), breaking every other write → invalid IL. Added a `hasUnknownWrite`
flag: a field/arg with any un-typeable write stays `object`. General fix, and the largest single
`realBug` reduction.

**#3 — `DisplayClassCleaner` hardening.** `PruneReferencedRemovals` (a fixpoint reference check:
never remove a field/method still referenced by code that remains — a dangling `MemberRef` is invalid
metadata), plus a tightened null-check-guard match requiring the removal candidate's tail to be a
pure guard body (constant loads, branches, returns only), no real work. Defensive; the findings were
latent on this corpus.

**#4 — Reflection-proxy type confusion** (`realBug` 17/17/1 → 2/2/1). *Not de4dot's bug originally.*
Reactor declares reflection stubs as `instance` methods whose `this` never holds an instance of the
declaring type; obfuscated callers pass an arbitrary receiver as a weakly-typed `object` argument to
a *static* proxy dispatcher, e.g.
`ldarg.0; ldarg.1; ldarg.2; ldsfld <delegate>; call static Proxy(object, string, BindingFlags, D)`.
That verifies fine. Once `ProxyCallFixer` correctly resolves the dispatcher back to the real
*instance* target, the same stack shape (receiver + N args) is reinterpreted as
`call instance Target::M(...)` and the `object`-typed slot silently becomes a typed `this` — hence
mirror-image errors at both the stub and every caller. Confirmed by bisection (`--no-cflow-deob` and
`--dont-rename` both still reproduce → not cflow, not the renamer) and by direct IL diff of original
vs deobfuscated.

Fix: `de4dot.code/deobfuscators/FakeInstanceStubFixer.cs`, run from Reactor v4 `DeobfuscateEnd()`
*after* `ProxyCallFixer`. It rewrites such a stub to `static` with the receiver as an explicit leading
parameter typed to the target's declaring type. This needs **zero IL edits**: static `arg0` occupies
the old `this` slot so `ldarg` indices already line up, and call sites push identical values
(receiver + N args == N+1 static args) against the MethodDef, so the call re-emits automatically.
Guarded to only rewrite methods *already provably invalid* — it requires the declaring type to be
non-assignable to the target's declaring type, so legitimate forwarding (a subclass calling a base
method) is untouched; ctors, virtuals and generics are excluded. Verified:
997 methods before and after, zero empty bodies, S3 unaffected (0 stubs matched), and the decompiled
C# goes from `((Type)this).GetMethod(...)` on a static class to the obviously-correct
`type_0.GetMethod(def, pol)`.
→ **Lesson: some "de4dot bugs" are the input's lies becoming visible. Check the original first.**

**#4b — Live methods deleted as unused** (`realBug` 2/2/1 → 0/0/1). `DotNetUtils.GetMethod2` could
not resolve a call to a method on a *generic instantiation of a type in the same module*. Such a call
is a `MemberRef` whose `DeclaringType` is a `TypeSpec` (e.g. `C\`1<!T>::M(...)`). The scope type
resolves to the correct `TypeDef` and `FindMethod(name)` finds the method, but
`FindMethod(name, MethodSig)` does **not** match that MemberRef's signature, so the lookup returned
null — confirmed by instrumentation: `scopeIsTypeDef=True sameModule=True findByName=True
findBySig=False`. `UnusedMethodsFinder` uses that lookup to decide whether a method is still
referenced, so every such call site was invisible, the callee looked unused, and it was deleted,
leaving dangling `MemberRef`s. Fix: `GetMethod2` now asks dnlib to resolve the reference
(`ResolveMethodDef()`, also unwrapping `MethodSpec`) before falling back to the scope-type +
signature lookup. Shared utility, so it helps every deobfuscator reasoning about liveness. Verified:
22 previously-deleted-but-referenced methods retained on S2, zero empty bodies, type count unchanged,
dangling-MethodRef errors to 0 on all three samples.
→ **Lesson: de4dot logged its own `ERROR: Could not resolve MethodRef` for exactly these, for months.
Read the tool's own warning output.**

**#4c — `DelegateCtor` on a delegate-pinned method** (`realBug` 0/0/1 → 0/0/0). `TypesRestorer`
narrowed a parameter from `object` to its real type while that method was being used to build a
delegate whose type argument still said `object`, so the delegate ctor no longer verified.
Concretely: `ShowAsContextPost(object)` → `ShowAsContextPost(GenericMenu)`, but the wrapper stayed
`Action\`1<object>` (built via `ldftn` + `newobj`) and the enclosing `NewReg<object>(Action<!!0>)`
instantiation was not updated either. The narrowing is correct in isolation; nothing rewrote the
delegate side. Identified by bisection: `--dr4-types false` → 0 errors, `true` → 1. Fix:
`TypesRestorer.FindDelegateBoundMethods()` collects every `ldftn`/`ldvirtftn` target up front and
`DeobfuscateMethods()` skips signature updates for those methods — leaving them consistent with the
delegate, which is the pre-restore state and always valid. Cost measured, not assumed: S3 has 671
distinct delegate-pinned methods but `TypesRestorer` was narrowing only **one**; the whole-assembly
IL diff is 4 lines. A future improvement could rewrite the delegate's generic argument and the
enclosing generic-method instantiation to match instead of skipping, recovering the nicer
`GenericMenu` type, but that means rewriting a TypeSpec and a MethodSpec chain — much riskier for one
method's readability.
→ **Lesson: a signature is a contract with every use site, including `ldftn`.**

**#4d — Methods that never terminate** (21 → 0). Some methods decompiled to `while (true) { ... }`
with no exit at all, **invisible to `ilverify`** because an infinite loop is type-safe. One root cause
in three places: a switch rewrite redirects each predecessor of the switch block to its resolved
target; redirect the *last live* predecessor and the switch becomes unreachable, and so does every
unresolved case — which can hold the method's only exit. The blocks still exist at that instant, so an
"is there still a `ret`?" check passes; the next iteration's dead-block cleanup then removes them.
The measurement change that cracked it: check whether an exit is **reachable**, not whether one
exists. Fixed in:

1. `XorSwitchDeobfuscator`/`SwitchRewriter` (Reactor-specific) — `WouldOrphanMethodExit` simulates
   the pending redirects on a copy of the successor map and skips the dispatch if no `ret`/`throw`
   would stay reachable. Corpus 21 → 19.
2. `SwitchCflowDeobfuscator` (de4dot's **shared** switch pass) — same defect, found by instrumenting
   `BlocksCflowDeobfuscator`'s loop after every step and every `IBlocksDeobfuscator` by name.
   `DeobfuscateTOS`/`Ldloc`/`StLdloc` all had it. Corpus 19 → 6.
3. The combined-effect gap: within one `DeobfuscateTOS` call the `Tos_Ldloc` fallback and the direct
   redirects were validated *separately*, each against a graph missing the other's edits. Split all
   three workers into a **plan** phase (`PlanTOS`/`PlanLdloc`/`PlanStLdloc`, which never touch the
   graph) and a single **apply** phase (`ApplyPlan`), so a call unions its own plan with the recursive
   fallback plans and validates the union once. Corpus 6 → 0.

Rewrites are modelled as a `SwitchRewrite` value (branch / branch+pop / Bcc), so `Bcc` sources
correctly keep the edge that does not point at the switch. Plans are deduplicated by source, since one
block can appear in both its own plan and a recursive one. All gates held corpus-wide throughout
(2 of the 3 sites are shared code): `realBug` 0/0/0, zero empty bodies, method counts unchanged
(S1/S2 1019, S3 2859).
→ **Lesson: check *reachability*, not existence. And validate a rewrite plan as a whole — it is
always the last edit that does the damage.**

**#6 — `FindSimplePath` ambiguity.** It returned the first BFS path and never detected that several
paths existed, despite its doc claiming otherwise. Callers *replay* that path to derive a state seed,
so the wrong path yields a wrong-but-in-range seed and a wrong edge. Now bails out when a second
distinct predecessor can reach the target — it fails closed, leaving the edge unresolved. Also
memoised per (start, target) for the resolver's lifetime (safe: the CFG is not mutated while edges are
being resolved), the duplicated "pop TOS, validate case index, read stateVar" tail extracted to
`ReadSeedAndCaseIndex`/`ReadCaseIndex`, and a stale doc comment ("30 blocks" vs `maxBlocks = 100`)
rewritten. Measured cost of the ambiguity guard: **+1 residual `switch` on S1 and S2, 0 on S3**, and
±3 in short-vs-long branch encoding; all gates unchanged. The large raw IL text diff is offset
renumbering — the opcode histograms differ only in `br.s`/`brfalse.s` encoding buckets.
→ **Lesson: "defensive" review findings can be correctness bugs wearing a disguise.** This one was
initially filed as cosmetic; it is the same silent-wrongness family as the phase-6 double-apply below.

**Two XorSwitch fixes recorded separately** because they predate the numbered queue:

- **Self-loop guard** (`SwitchRewriter`): never redirect a block to itself. A basic block has no
  internal branch, so `payload; br self` is an infinite loop regardless of retained payload; leaving
  the edge unresolved yields a recoverable goto instead.
- **Phase-6 double-apply** (`EdgeResolver`): the forward stateVar trace yields the value *at the
  predecessor's entry*, but `TryResolveEdge` re-emulated the backward chain and applied the
  predecessor chain's affine update a second time → a wrong (but in-range) case index → wrong control
  flow. Added `seedIsAtPredecessorEntry` to emulate only the predecessor.

**Generic string/constant decryption** (pre-existing, validated): `GenericConstantDecrypter` +
`GenericConstantInliner` handle v6.x generic constant decryption (`!!0 smethod_N<T>(int32)`) —
extract `mul`/`xor`, compute `(arg*mul)^xor`, top-2-bit type flag, bottom-30-bit offset, read from the
data blob. All encrypted string calls resolved.

### The pattern underneath all of them

> **A rewrite that is locally correct but globally wrong, producing output that passes every
> type-level check.**

Which is why the working discipline is **plan → validate the whole plan → apply**, never
apply-then-check. There is no undo: the rewrites mutate instructions
(`ReplaceLastInstrsWithBranch`, an added `pop`), so restoring successors alone leaves blocks
inconsistent.

---

## 4. The gate hierarchy — and what each gate is blind to

Run in this order. Each catches something the one above cannot.

| # | gate | catches | **blind to** |
|---|---|---|---|
| 1 | `ilverify`, complete refs | type-unsafe IL | anything type-safe |
| 2 | empty method bodies | deleted live code | code that is wrong, not absent |
| 3 | stack underflow | unbalanced CFG edits | balanced-but-wrong flow |
| 4 | no `ret`/`throw` in body | orphaned exits | exits reachable in CFG but not in execution |
| 5 | **state-machine trace** | mis-resolved dispatch | non-constant transitions |
| 6 | **metadata round-trip** | tokens no consumer can resolve | semantics; it only proves the module loads |
| 7 | **decryption coverage** | a decrypter that produced no data | strings that decrypt to wrong values |

**The known blind spot across all seven.** A rewrite that deletes a live branch but leaves the method
verifiable and terminating passes every gate, and shows up in the readability signals as an
improvement. Demonstrated by the abandoned opaque-predicate fold (§7 item 4) — read it before
trusting any change whose evidence is "fewer instructions, fewer unresolved dispatches".

> **The rule these gates do not enforce on their own:**
> **a green result is only meaningful if the measurement actually covers the property the change can
> affect.**
>
> This has now cost two reverts of *correct* work in the same session, in two different disguises: a
> fix that removed 274 fields and moved nothing else read as a no-op because the printed summary
> omitted the field count, and a comparison drawn between artifacts produced outside the shared
> pipeline, which were not comparable to each other at all. Neither was a gate failing; both were a
> gate not looking. Before accepting any change, name the property it can affect and check that
> something observes it — and if nothing does, that is the first thing to build, not the last.

**Gate 1** — see §2 for the two caveats (complete reference set; compare against the original).

**Gate 4 caveat.** A valid exit is `ret` **or** `throw`/`rethrow`. Counting only `ret` reports false
positives on compiler-generated iterator `IEnumerator.Reset()` stubs, whose entire body is
`newobj NotSupportedException; throw` — 4 of them on S3, which is why #4d's true starting point was
21 rather than 25.

**Gate 5 is in-tree.** `StateMachineTracer` in `de4dot.blocks/cflow`, reported from the Reactor v4
`DeobfuscateEnd`. It traces IL directly, so detection does not depend on a decompiler.

**Gate 6 note.** Gate 6 exists despite `ilverify` having *also* flagged the dangling token that
motivated it (the miss was the harness parser's — see the table below). It stays because `ilverify`
and a real metadata consumer test different assumptions, and there is now a concrete assembly, from
the reverted transaction in §7 item 1, that demonstrates the value of running both.

### When a zero is trustworthy

> **A result is trustworthy only when the tool completed successfully, all output was classified,
> unrecognised diagnostics fail closed, and the scope of the assertion matches the scope of the
> possible regression.**

This is not abstract. Five confident green results in this project each violated exactly one clause —
five instances of one rule, not five unrelated mistakes:

| what produced the green result | clause violated |
|---|---|
| incomplete reference set — `ilverify` silently skipped methods | all output classified |
| error regex required a non-empty kind, so `Error []:` never matched | unrecognised diagnostics fail closed |
| `de4dot` exit code unchecked, a stale artifact read as fresh output | tool completed successfully |
| decryption budget aggregate-only — one assembly can triple behind another's improvement | scope of assertion matches scope of regression |
| gate 5 summary logged only when it found something, so "no warning" read as "zero" | all output classified |

The fourth clause is the subtlest, and it was violated by a gate written *in response to* the other
three: an aggregate ceiling of 127 over a 35/35/27 baseline passes while one sample regresses 27 → 90
and another falls to 0. Fixed by enforcing per-assembly ceilings, with the aggregate derived from them
so the two cannot drift.

The fifth is the same lesson applied to gate 5 itself, and it is why `VerifyStateMachines` now reports
its summary at normal verbosity **even when it finds nothing**. A gate that goes silent on success
cannot be told apart from a gate that did not run, so anything reading the output scores a missing
trace as a clean one. State a passing result out loud; silence is not evidence.

Each looked like a clean result and each was measuring nothing. **The measurement layer has been a
richer source of wrong conclusions here than the deobfuscator itself**, which is worth remembering
before trusting any new green number.

---

## 5. The contained defect: the state a dispatch predecessor is entered with is not representable

This was "7 mis-resolved state machines", the class that stood between here and functional. Gate 5
now reports **0** of them, but by *containment* (§6), not by a fix. It is recorded in full because
any future attempt to resolve these machines has to get past it.

> **Read this before touching `EdgeResolver` or `DispatchDetector`.** The visible symptom is a
> transform block seeded with the state its own transform produced. That is downstream of an
> order-dependent `BuildBlockToCase` attribution — but **correcting the attribution cannot solve these
> methods**, because the state they need is *relational*: inner dispatch state **plus** which arm of
> the outer dispatch fired. `DispatchNode` models one switch and one `StateVar` and cannot represent
> that pair. Do not write another `BuildBlockToCase` rule or another seed guard; four have now failed,
> and the last one failed for a reason that no fifth can avoid. The evidence is below, in order:
> symptom, true cause, the dominance experiment, and the representation limit.

**Shape.** A dispatch loop where the traced state sequence never reaches an exit:

```csharp
int num = 3;
while (true) {
    switch (num) {
    case 3: radius   = ...; num = 2; break;
    case 2: height   = ...; num = 6; break;
    case 6: position = ...; num = 7; break;
    case 4:
    case 7: rotation = ...; num = 4; break;   // 7 → 4 → 4 → …  never exits
    case 5: shapeType= ...; num = 0; break;   // unreachable
    case 0: return;                            // unreachable
    }
}
```

**Why every gate below 5 passes it.** The `ret` is a switch *target*, so it is reachable in the CFG.
The IL is type-safe. Nothing is deleted. Only tracing the state variable reveals it.

**Why it is the worst class.** A mis-resolved machine does not look broken — **it looks like a shorter
method**. Every statement on the unreachable tail silently disappears from the decompiled output. In
the example the correct chain is `3→2→6→7→5→0`, and `shapeType` is simply absent. This has already
produced a wrong reconstruction from the decompiled output.

**Localisation** (four hypotheses refuted to get there):

- Not the predecessor-redirect path. Gates were built into both the Reactor-specific rewriter and the
  shared switch pass; both fired on real rewrites, neither changed the count. Reverted.
- Not switch-table corruption. Instrumenting `switchBlock.Targets` before and after every pass showed
  all targets stay distinct throughout.
- Not seed ambiguity in the brute-force phase. `EdgeResolver` phase 3 brute-forces `allSeeds`, filters
  by `VerifySeedRoutesToCase` (which only constrains `seed % M == ci`, so many seeds pass), and
  `break`s on the first yielding an in-range case index — which looks exactly like a state-dependent
  answer collapsed to one guess. Instrumented to report, per predecessor, how many *distinct* case
  indices the admissible seeds would produce: **2715 guesses, 0 ambiguous.** Every seed set agrees.
- Not a predecessor being associated with a case whose body it is not. Instrumented; that holds in the
  *working* methods too.
- **Localised to a single wrong constant, by reading the IL.** In the smallest offender the state is
  carried on the *evaluation stack* and popped by `switch`:

  ```
  IL_0012: ldc.i4 2 ; br IL_0025                    // entry
  IL_0019: call RemoveManager(2,0) ; IL_0020: ldc.i4 1
  IL_0025: switch (IL_003e, IL_005a, IL_003e, IL_003e, IL_0019)
  IL_003e: <payload> ; IL_0053: ldc.i4 0 ; br IL_0025
  IL_005a: ret
  ```

  Only `2` and `0` are ever pushed and both index the payload block, so the emitted IL loops forever.
  `ret` (index 1) is only pushed inside index 4, and **nothing pushes 4**. Decoding the original
  affine dispatch gives the true machine in two steps: payload once, then `RemoveManager`, then `ret`.
  de4dot gets step 0 right and step 1 wrong.

**Root cause, confirmed in code.** The affine-transform block is seeded with the state its *own*
transform produces, so the transform is applied twice. Per-edge logging on the smallest offender:

```
pred=<seed push>     seed=none/zeroed  -> case=1  targetIncomingState=1548872185   correct
pred=<affine xform>  seed=1198895877   -> case=2  targetIncomingState=-1878369011  WRONG (should be 0)
```

`1198895877` is exactly what that block computes from state `1548872185`. The seed comes from the
algebraic-extraction phase, whose bookkeeping is *correct* — that value really is the next case's
entry state. **The defect is the consumer**, which uses it to seed the block that produced it. (An
earlier "the two numbering spaces are mixed" hypothesis is refuted; `0` appearing in both roles was a
coincidence. Independently recomputing the derived seed with explicit 32-bit wrapped semantics
reproduces de4dot's logged value bit-for-bit, so the emulator's multiply / XOR / unsigned-remainder
semantics are not where the defect lives either.)

**Fix attempt, reverted.** Tracking seed provenance and refusing to seed a block with its own output
fixed two methods and **broke a third** that had previously resolved to clean straight-line code,
turning it into a non-terminating loop. Every other gate held. Reverted: a correct method made
non-terminating is strictly worse than the two gained.

**Why that is too coarse:** a block can legitimately be re-entered carrying the state it just produced
— that is the loop going around. Several case labels sharing one body makes one block the body of
multiple case indices, each arrival with a different valid entry state.

**Where the wrong seed actually comes from — traced, not inferred.** `EdgeResolver` now has a
provenance log (`DE4DOT_XORSWITCH_TRACE=<substring of the method's full name>`), which prints every
seed it acts on together with where that seed came from, and prints the BFS route by which each block
was attributed to a case. Off by default and verified to leave every gate bit-identical. On
`AdvisorTemplate::.ctor` it prints:

```
attribution: b4@IL_0066 <- case 0 via b0@IL_0041(brfalse.s) -> b2@IL_0025(switch) -> b4@IL_0066(xor)
pred b4@IL_0066 ownedByCase=0 [ldloc.s; ldc.i4 -593078698; mul; ldc.i4 1183106608; xor]
  <- src b2@IL_0025 last=switch
  <- src b3@IL_003E last=nop
phase1: pred=b6@IL_00A2 seed=<none>     -> case=1 targetIncomingState=1548872185
phase3: pred=b4@IL_0066 ownedByCase=0 trying 1 seed(s) [1548872185]        (rejected)
phase5: caseStateVar[0] = 1198895877
phase2: pred=b4@IL_0066 seed=1198895877 -> case=2 targetIncomingState=-1878369011   WRONG
```

Read the attribution route: case 0's search reached the transform block **through `b2`, which is the
outer, still-unresolved `switch`**. So `ownedByCase=0` was never a fact about the CFG. Two compounding
defects in `BuildBlockToCase` produce it:

- its per-case BFS traverses *through* any other dispatch block, so everything downstream of a second
  switch is attributed to whichever case reached that switch;
- on meeting a block another case already claimed it marks it ambiguous and **stops exploring that
  branch**. Since every case body flows back into the shared dispatch region, the lowest-numbered
  case's search claims most of the method and every other case's search dies at its own entry block.

The map is therefore an artifact of iteration order. Everything downstream trusts it: phase 3 checked
the *correct* seed 1548872185 against the *wrong* case with `VerifySeedRoutesToCase` and rejected it
(1548872185 % 3 = 1, not 0), then phase 2 took `caseStateVar[0]` — a correct value for case 0's entry
— and used it as the *predecessor's* entry state with no check at all. Hence the double-apply. The
earlier reading, "the defect is the consumer", is right about the symptom and wrong about the cause:
the consumer is applying a rule that is sound given a correct attribution.

**Fix attempt: replace reachability with dominance — IMPLEMENTED, MEASURED, REVERTED.** Dominance is
the relation every consumer actually assumes ("control cannot be here without having come through
that case target"), it is order-independent, and it refuses attribution for any block reached through
a foreign dispatch. It did exactly that on both target methods. Corpus-wide it is a disaster:
undecidable **16 / 14 / 38 → 168 / 174 / 394**, instructions +14 %, and **gate 5 fails** with two
non-terminating methods on S3. Decoupling the second consumer of the map (the cyclicity test in
`DispatchDetector`, which legitimately wants reachability, not dominance) changed none of it. Entry-
rooted dominance is far stricter than these machines need, and `Block.GetTargets()` carries no
exception edges, so in any method with handlers it degrades further. Reverted.

**Conclusion — this is a representation limit, not a seeding bug.** In both methods the affine
transform block's *only* predecessors are the outer dispatch:

| method | transform block | its predecessors |
|---|---|---|
| `AdvisorTemplate::.ctor` | `ldloc V_0; mul; xor` | the outer `switch`, and a `nop` fed by it |
| `CloneSystem` | `pop; ldloc V_0; mul; xor` | two blocks pushing *different* constants, selected by the outer machine |

So the state on entry to that block is a function of **(inner state, which arm of the outer dispatch
fired)**. `DispatchNode` models one switch with one `StateVar` and has no way to name the pair, which
is why no attribution rule — reachability, dominance, or anything between — can supply the seed. The
second method makes it starker: its transform block begins with `pop`, consuming a constant its
predecessor pushed, so it cannot even be emulated standalone.

That is the same object as the two-variable chained dispatch (§7 item 3 / `WORKLOG` #5), reached from
the other side. **Resolving these machines is that task, not this one**, and the sound outcome for the
derivation in isolation is to resolve *fewer* edges, not more: stop deriving the wrong ones and leave
the dispatch unresolved, which is what §6 already selects. Any future attempt should start from the
provenance log above rather than re-deriving the attribution defect.

---

## 6. How the 19 were contained: branch-and-select

`ObfuscatedFile.SelectDispatchCandidate`. For a method whose input body contains a `switch`:

```
DeobfuscateMethodBegin (proxy fixing) -> commit to the body -> snapshot it
├─ candidate A: every body-local pass, dispatch resolution ON
│    trace the finished method; if it does not loop, keep it and stop
└─ candidate B: restore the snapshot, same passes, dispatch resolution OFF
     keep it
only then: module-wide liveness / helper removal
```

Both candidates run the *whole* body-local pipeline — cflow, boolean inlining, `OptimizeLocals`,
`RepartitionBlocks`, string decryption, `DeobfuscateMethodEnd` — so whichever is selected is
internally consistent, and neither can leave a reference to something a later pass deletes. Three
details carry the correctness:

- **The snapshot is taken *after* `DeobfuscateMethodBegin` is committed to the body**, not before.
  Snapshotting earlier is what the reverted rollback attempt did (§7 item 1), and restoring a
  pre-proxy-fix body resurrected deleted proxy methods and produced an assembly no decompiler could
  open.
- **Locals are part of the snapshot.** `Blocks` holds a live reference to `Body.Variables` and
  `OptimizeLocals()` prunes and reorders it in place, so restoring instructions alone would leave
  `ldloc.2` addressing a different local. `CopyBody` already remaps branch targets, switch target
  arrays and every handler boundary including `FilterStart`.
- **Suppression is the redirect only** (`ISwitchDispatchResolver`), not the whole pass: detection,
  opaque-constant folding and edge resolution still run in candidate B, so the two candidates differ
  by exactly the decision under test. Both `SwitchCflowDeobfuscator` and the Reactor
  `XorSwitchDeobfuscator` honour it.

Only a `Loops` verdict rejects. `Undecidable` is kept, because rejecting on it would discard good
resolutions on no evidence.

**Result: 19 → 0**, with rejections of 4 / 4 / 11 — one per previously-broken machine, none spurious.
`AdvisorTemplate::.ctor` traces `payload -> RemoveManager(2,0) -> ret`, replacing the `2 -> 0 -> 0`
loop. Instruction counts went *up* slightly (+91 / +74 / +237), the expected direction: rejecting a
bad resolution keeps the code that resolution had made unreachable. Type, method and body counts
unchanged; empty bodies stay at 0. Gates 1, 6 and 7 unchanged (35/35/27, 97 residual, 0 `ilverify`
errors, 0 unclassified diagnostics).

> **This overturned an earlier finding worth recording.** The tally used to be 0 terminating / 7
> looping / 18 undecidable, and that `TERMINATES = 0` was read as "partial resolution is reliably
> destructive". It is — but all 19 rejected methods trace as **terminating**, not merely undecidable
> (the corpus went 1 → 20 terminating while undecidable stayed at 68). So the unresolved form is not
> just "faithful because unjudged": the trace positively confirms it reaches an exit. What the empty
> middle actually meant was that the true machines were decidable all along, and the resolution was
> the only thing making them look otherwise.

**What "terminating" does and does not certify.** The trace establishes that the unresolved form
*reaches an exit*. It says nothing about whether that form is a **faithful** rendering of the original
method — a different and stronger property, checkable only by reading the original IL. Faithfulness
has been verified that way for exactly two of the rejected methods, `AdvisorTemplate::.ctor` and
`CloneSystem`: in both, the emitted outer/inner switch pair reproduces the original's structure and
the traced machine matches the original's decoded state sequence. **Do not generalise that to the
other rejected methods without doing the same comparison for each.** Termination is measured
corpus-wide; faithfulness is not.

Branch-and-select stays the containment mechanism regardless of §5. It rejects a globally wrong
result without needing to reconstruct the chained machine, which is precisely why it works where four
attempts at deciding the same question locally did not.

---

## 7. Roadmap to "fully functional"

Ordered by value. Each step must hold every gate in §4.

1. ~~**Port the state-trace into de4dot as gate 5.**~~ **DONE** — `StateMachineTracer`, see §4.

   > **One failed approach, recorded so it is not retried: a speculative-transform transaction.**
   > Shape: snapshot the body before the control-flow passes, transform, trace the result, restore the
   > snapshot when the trace proves non-termination. The gate worked perfectly — non-terminating went
   > 19 → 0 with those methods moving to *undecidable*, and empty bodies, no-exit methods and
   > `ilverify` all stayed at zero. It still had to be thrown away, because the restored assembly
   > **cannot be decompiled**: `ArgumentNullException (Parameter 'methodReference')` in ILSpy's
   > `DecodeCall`. A body snapshotted before the per-method passes predates proxy-call fixing and
   > string decryption, so restoring it resurrects calls to proxy and inlined methods that
   > `DeobfuscateEnd` has already committed to deleting, leaving the token dangling.
   >
   > **So the transaction is not atomic with respect to module-level removals**, and no amount of care
   > inside `CilBody` fixes that — the state needing rollback lives outside the body. `CilBody` is not
   > a sufficient transaction boundary; the selected unit is (body + whatever module-level effects that
   > body's passes queued). Two ordering constraints follow, and §6's design obeys both:
   >
   > - **Helper/proxy removal must run after selection**, or candidate B retains references an earlier
   >   liveness analysis already marked for deletion — exactly the dangling token above.
   > - **Producing the candidates must not commit shared side effects.** The candidate region has to
   >   be body-local and deterministic. Any pass in it that mutates module-level registries, removal
   >   queues, renaming state, caches or helper usage counts must either be deferred until after
   >   selection, or be cloned and selected alongside the body.

2. ~~**Fix the mis-resolved machines.**~~ **DONE** via branch-and-select (§6): 19 → 0, all 19 moved to
   unresolved and traced as *terminating*, none to "resolved". The `EdgeResolver` seeding bug behind
   them (§5) is contained, not fixed — resolving them properly is now a readability item ranking with
   3–5 below, and any attempt has to pass gate 5 to land.

3. ~~**Two-variable chained dispatch.**~~ **DONE — resolved, and the export reviewed method by method
   against the original binary.** Branch-and-select rejections **19 → 0**; every changed decompiled
   method carries a recorded verdict. Some methods nest an **outer plain-int `switch(state)`** around
   the **inner affine xor-switch**; de4dot only recognises the inner layer. 8 sites in the corpus,
   currently left fully unresolved and therefore *correct*. A **readability** item, not a correctness
   one — which is why it ranks below the above despite being the oldest open task.

   **Three attempts, all reverted. Do not re-try blind.**
   - **Exp 1 — plain-header detection.** Recovered the state machine correctly (a representative
     method decoded to its exact linear form) and dropped dispatches/loops sharply, **but** left the
     bare `switch` block with no stack input → 592 stack underflows.
   - **Exp 2 — all-or-nothing + atomic dead-switch removal.** Metrics looked great **but** empty
     method bodies exploded — it was **deleting live code**. `FailedCount == 0` is not a true "fully
     resolved" signal: passthrough blocks are marked resolved without being rewired.
   - **Exp 3 — edge rewrites only, no explicit block removal (reachability cleanup).** Held the
     deletion and underflow guards **but** introduced ~184 new invalid-IL errors (`PathStackDepth`,
     `ReturnVoid` with a leftover state constant on the stack) — the plain state-update push is not
     cut, so the stack is left unbalanced.

   **The real fix (Exp 4, not attempted):** joint inner+outer resolution with **explicit stack
   rebalancing on every CFG edit** (cut the full state-update expression including its push; handle
   conditional/opaque state updates), **gated per-method by re-verification** so a rewrite is kept
   only if the method still verifies. Treat the current `realBug` baseline as the floor. Substantial,
   dedicated work — and note that an all-or-nothing decision has to be made **where the edges are
   derived** (`EdgeResolver`), not where they are applied. A gate rejecting any plan whose
   resolved-edge graph contains a deterministic exit-free cycle was built into both the Reactor
   rewriter and the shared switch pass, fired 19 times on real rewrites, and left the broken count
   unchanged: the cycles run through cases the plan never touches. The plan/apply split now in place
   is the scaffold this always needed.

   **§5 is the same object, reached from the other side, and it raises the price.** The machines
   branch-and-select contains are these machines: their transform block's entry state depends on which
   outer arm fired, so it is not derivable from the inner dispatch alone at any level of attribution
   precision. Scope this as a representation change — chained dispatch context, transform blocks
   shared between arms, and stack-carried state inputs — not as an `EdgeResolver` seeding fix.

   ---

   #### Design: relational dispatch modelling (not implemented — review before building)

   **What the machine actually is.** Both worked examples are *two dispatch sites in one
   mutually-recursive machine*, and the state is carried on the **evaluation stack**: the entry pushes
   a constant and branches straight to the `switch`, past the `ldloc` that would otherwise load it.
   The dispatch does `dup; stloc V_n`, so the local is a *copy* the next transform reads — not the
   carrier. Reduced from `AdvisorTemplate::.ctor`:

   ```
   entry:  ldc.i4 2 ; br OUTER                                  // pending = 2
   OUTER:  switch (XFORM, ret, SEED, PAYLOAD, ...)              // consumes pending
   SEED:   ldc.i4 1714725738                                    // pending = const, falls into INNER
   INNER:  ldc.i4 K ; xor ; dup ; stloc V_0 ; ldc.i4.3 ; rem.un ; switch
   XFORM:  ldloc V_0 ; ldc.i4 A ; mul ; ldc.i4 B ; xor ; br INNER   // pending = f(V_0)
   PAYLOAD: <work> ; ldc.i4 0 ; br OUTER                        // opaque-predicate gated
   ```

   `pending` alternates between an **outer case index** and an **inner pre-modulus value**. Those are
   different spaces, and the theory notes' old "two numbering spaces are mixed" confusion was exactly
   this distinction having nowhere to live.

   **1. State representation.** `MachineState = (Site, Pending, Locals)`, immutable, with structural
   equality:
   - `Site` — which dispatch block control is entering. This is the outer-arm context, and it is what
     makes the two value spaces distinguishable.
   - `Pending` — the abstract stack that site will consume, `Int32Value[]` of length
     `site.StackInputs`. `DispatchDetector.ComputeStackInput` already computes that length.
   - `Locals` — constants for the state locals of *every* site in the group, not one `StateVar`.

   **2. Stack-carried inputs.** `Pending` is carried along the explored edge, so nothing has to be
   re-derived from block attribution — which is what deletes §5's whole failure mode: no
   `BlockToCase`, no seed guessing, no `VerifySeedRoutesToCase`. The rewrite side matters just as
   much: **the producing push must be cut, not only the branch retargeted.** That is Exp 3's ~184
   stack errors precisely. Under this design it is automatic rather than surgical — emitted code is
   generated from the explored configuration, so the push is simply never emitted.

   **3. Shared transform blocks.** Rule: **a block is emitted once per distinct `MachineState` it is
   entered with.** In `CloneSystem` the transform block is entered from two predecessors pushing
   different constants (`-192729005`, `-295274214`) and begins with `pop`, consuming that constant —
   so it is two specialisations, not one block with one answer. Collapsing it to a single target is
   what "partial resolution is reliably destructive" has been describing all along. If a block would
   need more than *k* specialisations, or a configuration repeats without progress, **fail closed and
   leave the group untouched**.

   **4. Integration.**
   - `DispatchNode` is unchanged and keeps the single-site path. A new `DispatchGroup` collects sites
     that are mutually reachable through each other's case bodies; a one-site group delegates to
     today's code, so the large majority that already resolves is untouched by construction.
   - Plan/apply, reusing the existing split: the explorer emits a `SpecializationPlan` (blocks with
     their instructions, edges, entry redirect) that is validated **whole** before any mutation —
     per-block stack depth via `StateUpdateFinder.ComputeStackDepths`, every emitted block has a
     successor or terminates, at least one exit reachable.
   - The completeness signal is the explorer's own termination — it either enumerated a finite closed
     configuration set or it bailed. **Never `FailedCount == 0`**, which Exp 2 proved is not a
     resolution signal.
   - Gate 5 and branch-and-select stay exactly as they are, as independent post-hoc checks. Prediction
     worth testing: if this is right, branch-and-select *rejections should fall*, because fewer bad
     resolutions get built. A rise means the plan validator is wrong, not that the gate is noisy.

   **5. Smallest implementable slice — LANDED.** `RelationalDispatchResolver`: two or more sites,
   walked forward from the method's first instruction with a real emulator, every transition
   determined, **no payload block entered twice**, and a plan applied only after the walk reaches a
   real exit. Slice 2 adds specialisation (`CloneSystem` needs it); slice 3 relaxes determinism.

   Measured, diffing the **set** of rejected method identities rather than the count:

   | | before | after |
   |---|---|---|
   | branch-and-select rejections | 4 / 4 / 11 (19) | 1 / 2 / 2 (**5**) |
   | methods newly rejected | — | **0** |
   | gate 5 non-terminating | 0 / 0 / 0 | 0 / 0 / 0 |
   | gates 1 / 6 / 7 | pass, 35/35/27 | unchanged |
   | types / methods / bodies / fields | — | all unchanged |
   | instructions | 32572 / 32631 / 80257 | 32495 / 32560 / 80007 |

   `AdvisorTemplate::.ctor` — the acceptance case — leaves the rejection set and comes out as 47 bytes
   of straight-line code whose payload sequence is `Label(...)` → `RemoveManager(2, 0)` → `ret`,
   matching the sequence decoded by hand from the original binary. Termination alone would not have
   shown that; a wrongly *shortened* machine also terminates.

   Three defects found while building it, each by instrumenting rather than guessing, and each worth
   not rediscovering:
   - the walk stalled on Reactor's opaque predicates until it learned to follow a conditional whose
     constant the emulator already knows — that is determinism, not a fork;
   - it treated re-entry into a dispatch *site* as needing specialisation. Sites are re-entered every
     iteration by construction and carry no state of their own; only **payload** blocks are held to
     "entered once";
   - `StateUpdateFinder.ComputeStackDepths` measures from an assumed entry depth of 0, so every
     stack-carried state block reads as an underflow and its push could never be cut. The walk knows
     the real entry depth; it now passes it in.

   **Slice 2 — attempted, reverted, and the scope boundary is the finding.** Every one of the five
   remaining rejections refuses with `RevisitedBlock`, so specialising a payload block reached in two
   configurations looked like exactly the right next step. Built it: visit-indexed steps, a copy of
   the block per configuration, whole-plan validation before any mutation. It resolved **nothing** on
   the corpus — gates and rejection set byte-identical to slice 1 — and was reverted rather than left
   in as inert machinery.

   The reason is a shape the design did not anticipate. A Reactor payload does not occupy one block:
   the opaque predicate splits it, so `call A(); ldc.i4.0; br OUTER` is two blocks joined by a
   non-dispatch edge. Specialising it therefore needs every block of the payload copied, and the
   copies' internal edges rebuilt — which slice 2 explicitly excluded, refusing with "a specialised
   block on a non-dispatch edge". That exclusion is what makes it safe and also what makes it useless
   here: it excludes every real instance.

   **The unit of specialisation is a region, and "chain" is the wrong word for it.** A payload may
   branch and merge internally — opaque predicates alone guarantee that — so the model is:

   > a **maximal single-entry subgraph** whose exits all lead to a dispatch site, with **no external
   > entry into any internal block**.

   Single-entry ownership is the whole safety property. A block reachable from outside the region
   cannot be copied, because the copy would silently steal or duplicate an unrelated path.

   #### What the next attempt must specify

   1. **Region discovery** — from the arrival block, the maximal subgraph whose every internal block
      has no predecessor outside it, and whose exits are dispatch traversals.
   2. **Which internal edges are cloned and remapped**, and how a copy's internal targets are
      rewritten to the copy rather than the original.
   3. **How exits reconnect** — each exit carries its own configuration, so a region copy has one
      exit edge per traversal it terminates in.
   4. **How stack state is preserved across every internal edge**, not just at the boundary. The
      entry-depth bug in slice 1 was exactly this at one edge; a region has many.
   5. **Exception-handler boundaries and external branches** — a region may not straddle a handler
      boundary, and any external branch into it disproves single-entry ownership.
   6. **Caps**, declared up front and independently: region size, copies per region, emitted blocks,
      total emitted instructions.
   7. **Fail-closed when single-entry ownership cannot be proven** — refuse the region, not just the
      block, and leave the method to the existing path.

   #### What must be observed, beyond the correctness gates

   The gates in §4 cannot see most of what region specialisation can get wrong, so each of these
   needs its own explicit observation before the work is trusted:

   - **ownership** — every internal block had no predecessor outside the region;
   - **specialisation count** — copies actually made, per region and in total;
   - **emitted growth** — blocks and instructions added, against the declared caps;
   - **stack preservation across every internal edge**, not only at the region boundary;
   - **rejection-set identity** — the set, never the count;
   - **`CloneSystem`'s activation branch** — present by name, not inferred from a count.

   #### Outcome

   Landed in two slices. **Slice 1** (`RelationalDispatchResolver`) walks the machine forward
   carrying the configuration on the edge — no seed, no case attribution — and took rejections
   19 → 5. **Slice 2** specialises a region reached in more than one configuration, taking them
   5 → **0**. Every gate held throughout and no method ever entered the rejection set.

   The apply phase needed one correction worth remembering: whole-plan validation is not enough if
   materialisation *reads* blocks that earlier apply steps are already mutating. Copies must be taken
   from the pristine blocks before any rewrite — applying a validated plan has to be atomic with
   respect to its own inputs too.

   **The export was then reviewed method by method against the original binary**, not against the
   previous export. Nine changed decompiled methods reduce to six IL methods (a decompiler inlines a
   lambda body into every call site, so one changed method can appear as several); five are faithful
   outright and one is faithful conditional on the module-constant fold, with its alternative trace
   recorded. Downstream tooling grew a changed-method deriver and a read-only original-machine trace
   generator to make that review reproducible rather than a one-off reading.

   #### Acceptance for the next attempt

   - `shared_payload` resolves to exactly `A()`, `A()`, `B()`.
   - `CloneSystem` leaves the rejection set **with its activation branch intact** — the branch an
     earlier experiment deleted, so its presence is the check that this one did not.
   - The rejection set decreases from five, with **zero** new entries.
   - Gate 5 stays `0 / 0 / 0`; gates 1, 6 and 7 unchanged.
   - Unsupported regions are left untouched, verified by `call_dependent` and `two_site_linear`
     still passing.
   - Emitted growth stays inside the declared caps.

   **Fixtures: done.** `tests/run_xorswitch_tests.py` plus three fixtures under
   `tests/samples/xorswitch/`, 3/3 passing. Portable, because `test.ps1` cannot run outside Windows
   and its byte-comparison against checked-in `.cleaned.il` is not reproducible across ildasm builds;
   these assert what the resolver *decided* instead.

   **6. Regression tests.** `tests/samples/inlining/` already assembles `<name>.il`, runs de4dot, and
   diffs against `<name>.cleaned.il` — the right harness, and there is no other. Add shape fixtures
   distilled from the corpus (shapes only, no sample bytes): a two-site linear machine, a shared
   transform entered with two states, and a **negative** case whose transition depends on a call and
   must be left untouched. Corpus acceptance stays the scorecard, with the opaque-predicate dispatch
   count as the readability delta.

   **7. Abandon criteria, fixed in advance.** Revert if slice 1 raises gate 1, empties any body,
   raises branch-and-select rejections, or raises the non-terminating count. The current baseline is
   the floor; a partial improvement bought with any of those is the exact trade the three failed
   experiments each made.

4. ~~**Opaque-predicate folding with a zero-seeded local.**~~ **ABANDONED — built, measured, reverted.
   Do not retry as specified; the premise is false.** The other Reactor dispatch shape is
   `switch((num = (num*A)^B) % k)` where `num` reads as an un-stored, `.locals init`-zeroed local —
   **37 sites in the corpus**. (Counting them needs a metric matching *this* form: a `switch` whose
   operand is the affine update expression. A plain `switch(<local>)` count does not match it and
   reports far fewer.) de4dot models the zero in `cached_zeroed_locals` but only applies it when
   emulating from the method's first instruction. The plan was to generalise that to "no block that
   can reach the dispatch writes the local, therefore the zero still holds".

   **Two reasons it does not work, in increasing order of importance.**

   *The rule as written is unsound, and the fix for that is cheap.* The dispatch block writes the
   state local and is normally its own successor, so the zero describes only the block's **first**
   execution while folding the terminator rewrites every execution. The full "must be zero on every
   entry" dataflow yields nothing at all, because a sound must-analysis always kills the zero — any
   win requires reasoning about the first execution specifically. A guard makes that sound: accept the
   fold only if the target it selects cannot lead back to the block, which is self-justifying because
   after the fold every path out of the block goes through that target. Model exception edges by
   treating every source-less block (handler and filter entries) as reachable from everywhere.
   Without the guard the first site tested (`Definition::ComputeWrapper`, S3) folds to the dispatch
   block itself: an infinite loop and a gate 5 regression. With the guard, **36 of the 37 sites are
   correctly refused**, because their machines take 2–3 junk iterations before leaving the loop —
   resolving those needs the loop **peeled** (a duplicated dispatch block per iteration), not one edge
   folded. So the readability ceiling was never "a large share"; it was one site.

   *And that one site folded wrongly.* `.locals init` zero is a fact about the **original** method. By
   the time a body reaches this pass it may have been partially resolved, and de4dot's own block
   merging can leave a dispatch block reading a state local nothing stores — because the store was in
   a different block the resolution discarded. Verified on `ConfigurationTestStub::CloneSystem` (S1):
   in the original the inner machine is seeded **on the evaluation stack** (`ldc.i4 783551359` pushed
   by the outer machine's case body), `stloc.s 0` and the matching `ldloc.s 0` sit in two different
   blocks, and the load is only ever reached after the store — the init zero is never read. In
   de4dot's rewritten body the two collapsed into one block, the seeding push is gone, and `ldloc.0`
   reads the zero. Folding on it resolves the dispatch to `ret` and **deletes a live
   `MapSystem(...)` branch** that the original reaches whenever `LogoutSystem()` takes the other side
   of its test.

   **The gate hierarchy did not catch it, and that is the durable finding.** Gate 1 saw type-safe IL,
   gate 2 a non-empty body, gate 5 a *terminating* machine, gates 6 and 7 no change. The scorecard
   diff read as an improvement in both directions that matter: S1 undecidable 16 → 15 and six fewer
   instructions. §4 records the resulting blind spot, and it is the same shape as every bug in §3.

   It also sharpens §6: containment of the seeding bug is weaker than "unresolved but faithful" —
   `CloneSystem` is a body that reads an uninitialised local where the original read a computed state.
   Nothing currently detects that, and any future pass that trusts an initial-value claim over a
   partially-resolved body will hit the same trap. **Fix the seeding first; the zero is only
   meaningful in a body whose stores are still all there.**

5. **Closure/lambda inlining.** Was worded "nested `<>c__DisplayClass` closures are not recursively
   inlined by the decompiler". Measured, and the premise was wrong: nesting was never the dominant
   cause. A closure the decompiler inlines vanishes from the output, so every closure type still
   visible is one that was not inlined, and the shape of the type says why. Across the corpus:

   | cause | closure types | construction sites |
   |---|---|---|
   | writable static self-reference field (obfuscator residue) | 79 → **0** | 100 → **0** |
   | captures a parent closure (the "nested" case) | 15 | 27 |
   | neither | 51 → 115 | 0 → 73 |

   **The residue column was de4dot's and is now fixed.** Reactor injects, per closure type, a
   writable static self-reference field plus *two* static helpers that read it — a `bool` null-check
   guard and a plain getter returning the declaring type. `DisplayClassCleaner` recognised only the
   guard, so the getter survived as a "remaining" referencer and `PruneReferencedRemovals` kept the
   field; the field is what stops the decompiler recognising the type as an inlinable closure. The
   getter is now recognised too. Verified against the original binary on the smallest case
   (`<>c__DisplayClass66_0`: field `RestartFactory`, guard `MoveFactory`, getter `RunFactory`).
   Gates all unchanged; the only metadata movement is **−274 fields** across the corpus, and the
   canonical export loses 166 lines, all of them those field declarations.

   **What remains is not de4dot's to fix.** 15 types capture a parent closure — a decompiler
   limitation, not obfuscation, and the emitted C# is correct, only verbose. Merging two closure
   types into one to work around it would rewrite structure the *original source* genuinely had.
   The 115 "neither" types are likewise the decompiler declining to inline for its own reasons.
   **Closed**: the actionable part is done, the residual is presentation, and 15 sites does not
   justify a new IL pass.

   > The measurement is `analyze_closures.py` in the downstream tooling, and it is the reason this
   > entry could be settled at all. It also caught the reverse error: the fix above was briefly
   > reverted because the acceptance check's *printed* summary omitted the field count, so a change
   > that removed 274 fields and moved nothing else was eyeballed as "no effect". The JSON had it all
   > along. Two lessons, both already paid for: a readability claim needs a metric that can see the
   > thing it claims, and "no change" from a check that does not measure the change is not evidence.

### Definition of done

- Gates 1–5 all zero across the corpus.
- Every remaining unresolved dispatch is *faithful* — verified by the state trace, not assumed.
- No pass can produce output that passes gates 1–4 while being semantically wrong; i.e. gate 5 is
  in-tree and enforced, rather than left to whatever inspects the output afterwards.

---

## 8. History: the net8.0 pin and the extraction worker

Closed, but the invariant at the end of this section is live.

`GenericConstantDecrypter.TryDynamicExtract` obtained Reactor's constant/string data array by calling
`Assembly.Load` on the **obfuscated target inside de4dot's own process** and running its `.cctor`.
.NET 10's loader validates nested-type metadata more strictly and rejects Reactor output outright:

```
BadImageFormatException: Enclosing type(s) not found for type '<obfuscated name>'
```

No data array, so nothing decrypted. Measured on the corpus: undecrypted `smethod_N` call sites went
**97 → 3777**, and plain string literals regressed to unresolved
`global::<Module>.smethod_N<string>(<key>)` calls. net8.0 extracted 15580 / 15580 / 37636 bytes from
the same three inputs. **Gates 1–6 were all green throughout** — verifiable IL, terminating machines,
a clean metadata round-trip, and nothing decrypted. That gap is why gate 7 exists.
`AssemblyLoadContext` cannot fix it: it changes dependency resolution, not which runtime validates the
image, and `Assembly.Load` throws for an image invalid under the *currently loaded* runtime.

**The real defect was that this ran in-process at all.** Two corrections found while fixing it:

> **de4dot's existing out-of-process mechanism does not exist on modern .NET.** `AssemblyData` plus
> seven `AssemblyServer*` projects were built on .NET Remoting plus AppDomains, so they are
> `#if NETFRAMEWORK` throughout. On net8.0/net10.0 *every* client factory —
> `NewProcessAssemblyClientFactory` included — falls back to `SameAppDomainAssemblyServerLoader`,
> whose `LoadServer()` is just `AssemblyService.Create(serviceType)`: a plain in-process object, no
> child process, no isolation. The `AssemblyServer*` hosts target **net48 only**. Genuinely reusable:
> `IUserGenericService` (`AssemblyLoaded(Assembly)` + `HandleMessage(int, object[]) → object`, already
> the right shape for returning a `byte[]`) and the `AssemblyData` service code, which multitargets
> `net48;net8.0`. What had to be new: a host executable for modern .NET, and a transport.
> **This also means de4dot's *dynamic string decryption* runs in-process on modern .NET.** In-process
> execution of hostile code is not a quirk of this fork's constant decrypter — it is the state of the
> whole dynamic path since the .NET Core port.

> **Static extraction is not an option: spike NEGATIVE on all three samples.** Dumping the array the
> working dynamic path produces and searching the original PE for it found not even a 16-byte prefix,
> so the array is **computed, not stored**. Entropy of 5.11 bits/byte on the *output* says decrypted
> or decompressed content rather than a blob lifted from the file, and neither a single-byte XOR nor
> any zlib stream in the image reproduces it. Recovering it statically means emulating whatever the
> `.cctor` computes — the "substantial IL emulation" case, where the decision rule says build the
> worker rather than grow an interpreter. Cheap and reusable as a technique: dumping the known-good
> array and searching for it verbatim answers "RVA-backed?", "transformed?" and "reproduces exactly?"
> in one test, without writing an extractor first.

**What landed:** net10.0 host, self-contained net8.0 worker in `constdata/`, one assembly per process,
fixed-shape binary protocol, pluggable confinement via `DE4DOT_CONSTDATA_SANDBOX` (bubblewrap by
default), and the fork-wide net8.0 pin removed — only `de4dot.constdata` is still pinned, documented at
`TryDynamicExtract` itself so a routine framework bump meets the explanation. Gate 7 holds at
**35 / 35 / 27** under the net10.0 host, which was the acceptance criterion.

**The live invariant, stated once because it is the thing worth not breaking:**

> Only failures that occur *before the target executes* may trigger an automatic in-process fallback.

`TargetFailure`, `ProtocolFailure` and `Timeout` all fail closed. Without that, a hostile target
escapes confinement simply by crashing, hanging, or corrupting the reply — the sandbox becomes
optional at the target's discretion. `DE4DOT_CONSTDATA_ALLOW_INPROC=1` is the deliberate escape hatch
for trusted input, and warns when used. All four paths are tested.

Note that a worker process alone is **not** a security boundary, and neither is `AssemblyLoadContext`:
the target's `.cctor` still runs with the worker's filesystem, network, environment, native-interop
and process-creation rights. That is what the confinement layer is for — separate user/mount/network
namespaces, read-only input, private tmp, `no_new_privs`, seccomp and cgroup limits on Linux;
AppContainer or another restricted token, no network capability, narrow filesystem grants and a Job
Object with kill-on-close on Windows.

Two follow-ups, neither touching that boundary:

- ~~**Validate the field token before `Module.ResolveField`.**~~ **DONE.** Validated in
  `ReadRequest`, beside the other request fields, so a structurally impossible token is a *protocol*
  failure — which fails closed — instead of something handed to `ResolveField` under a catch-all. A
  field token is `(0x04 << 24) | rid` with a non-zero rid; the parent only ever sends a real dnlib
  `FieldDef` token, so this fires on a parent bug and never on target behaviour. The scan fallback
  for a **well-formed** token that does not resolve in the loaded module is deliberately untouched.
- ~~**Drain stderr asynchronously.**~~ **DONE.** stderr was redirected and never read, so an
  undrained pipe fills at a few tens of KB and the worker blocks forever on write — the 60s timeout
  covered that, but reported a stall whose actual cause was a chatty target. Now drained via
  `BeginErrorReadLine` into an 8 KB bounded buffer (a diagnostic, not a transcript) and logged on the
  timeout and failure paths, turning discarded output into evidence.

Neither touches the fail-closed boundary: no `Outcome` assignment was added, removed or changed.
Verified by the canonical scorecard — every gate byte-identical to the pre-change baseline, with
gate 7 at 35 / 35 / 27 confirming the worker path still runs.

---

## 9. Working notes worth not rediscovering

- **An offset is not a block identity.** Instructions de4dot synthesises default to `Offset == 0`, so
  distinct rewritten blocks all print as `IL_0000`. A diagnostic keyed on offsets alone produced a
  confidently wrong conclusion here. Use a positional index alongside.
- **Bisect with the CLI before reading code.** `--no-cflow-deob`, `--dont-rename` and `--dr4-types
  false` each isolate a subsystem in one run, and settled three separate questions faster than any
  amount of static reading.
- **Then bisect *within* cflow by disabling one `IBlocksDeobfuscator` at a time** — that is what
  distinguished the Reactor-specific pass from the shared one.
- **Instrument, do not guess.** Every hypothesis resolved here was resolved by a print statement. Two
  that were resolved by reasoning alone were later refuted.
- **A failed fix is a result.** Several gates were built, measured, found to change nothing, and
  reverted. Reverting is correct: an unvalidated guard with a measurable readability cost does not
  belong in a pass with three failed experiments behind it. The *designs* are preserved above so they
  are not re-derived.
- **`realBug 0` is not "correct".** It means no type-unsafe IL. Say that precisely; the difference is
  exactly the defect in §5.

### Still-open notes from the Reactor v6 branch audit

The branch that added the v6 deobfuscator also modified **shared** code. Findings that are still live:

- **Noted (low priority):** `IDeobfuscator` gained a member — a breaking change for out-of-tree plugin
  DLLs that implement the interface directly (internal code is safe via a virtual default).
- **Latent:** `TrackedArrayValue` is a mutable `Value` (aliasing hazard if an array local survives
  speculative re-emulation).
- **Verified safe:** the dnlib 3.6→4.5 migration (incl. the CodeVeil resource-API edits), a
  `Resolver.cs` refactor, and the `Sizeof`/`Unbox`/`Rem_Un`/switch-refactor emulator changes.
- The pre-branch de4dot cannot process these v6.x samples at all, so the Reactor-path introduced bugs
  were new-feature imperfections, not regressions. The only confirmed shared regression was the shift
  guard (§3 #1).
