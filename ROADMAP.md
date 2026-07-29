# .NET Reactor v6.x — State of the Deobfuscator, and what "fully functional" needs

Goal: a deobfuscator whose output can be trusted as a faithful reconstruction of the original
assembly — not merely one that loads, verifies, and looks tidy.

`WORKLOG.md` is the task queue. `IMPROVEMENT_PLAN.md` holds per-fix findings and the failed-experiment
record. **This file is the synthesis**: what is known, what is measured, what is still wrong, and what
"done" would mean. Read it first when picking the effort back up.

Test corpus: three .NET Reactor v6.x assemblies — **S1**, **S2** (smaller, ~16 types each), **S3**
(large, ~70 types). Not in this repo.

---

## 1. Where it actually stands (measured, not asserted)

| property | state | how it is measured |
|---|---|---|
| Introduced invalid IL (`realBug`) | **0 / 0 / 0** | `ilverify` vs original, complete reference set, plugin-internal types only |
| Methods that never terminate | **0** | no `ret`/`throw`/`rethrow` anywhere in the body |
| Empty method bodies (deleted live code) | **0** | `Code size: 0` in the IL dump |
| Stack underflows | **0** | `ilverify` |
| Dangling `MemberRef`s | **0** | de4dot's own "Could not resolve MethodRef" output |
| **Semantically broken state machines** | **17** | state-trace from the seed — **see §4** |
| Unresolved two-variable dispatches | 8 | left unresolved, and *faithful* |

The first five rows are the historical definition of "correct" and they are all green. **The sixth row
is why that is not the same as functional.** Those 17 methods verify perfectly, terminate structurally,
and are wrong.

> The recorded baseline was "6/6/0" for a long time. That was measured with an incomplete reference
> set, and `ilverify` **silently skips** methods it cannot fully resolve, so it under-counts. The true
> pre-fix figure was **17/17/1**.

---

## 2. What was wrong, and what it taught

Five bug classes found and fixed. The root causes matter more than the fixes, because they rhyme.

**#4 — Reflection-proxy type confusion.** *Not de4dot's bug originally.* Reactor declares reflection
stubs as `instance` methods whose `this` never holds an instance of the declaring type; callers pass an
arbitrary receiver as a weakly-typed `object` argument to a static proxy dispatcher. That verifies.
Resolving the dispatcher back to the real *instance* target reinterprets the same stack slot as a typed
`this`. Fix: `FakeInstanceStubFixer` rewrites such a stub to `static` with the receiver as an explicit
leading parameter — zero IL edits needed, since static `arg0` occupies the old `this` slot.
→ **Lesson: some "de4dot bugs" are the input's lies becoming visible. Check the original first.**

**#4b — Live methods deleted as unused.** `DotNetUtils.GetMethod2` could not resolve a call to a method
on a *generic instantiation of a type in the same module* (a `MemberRef` whose `DeclaringType` is a
`TypeSpec`). `UnusedMethodsFinder` uses that lookup to decide whether a method is still referenced, so
those call sites were invisible and the callee was deleted, leaving dangling `MemberRef`s. de4dot was
already logging its own "Could not resolve MethodRef" for exactly these.
→ **Lesson: de4dot's own warning output contained the answer for months. Read it.**

**#4c — `DelegateCtor` on a delegate-pinned method.** `TypesRestorer` narrowed a parameter from `object`
to its real type while that method was being used to build a delegate whose type argument still said
`object`. The narrowing is correct in isolation; nothing rewrote the delegate side.
→ **Lesson: a signature is a contract with every use site, including `ldftn`.**

**#4d — Methods that never terminate.** One root cause in three places. A switch rewrite redirects each
predecessor to its resolved target; redirecting the *last live* predecessor orphans the switch, and with
it every unresolved case — which can hold the only exit. The blocks still exist at that instant, so an
"is there a `ret`?" check passes; the next iteration's dead-block cleanup then removes them.
→ **Lesson: check *reachability*, not existence. And validate a rewrite plan as a whole — it is always
the last edit that does the damage.**

**#6 — `FindSimplePath` ambiguity.** Returned the first BFS path and never detected that several paths
existed, despite its doc claiming otherwise. Callers *replay* that path to derive a state seed, so the
wrong path yields a wrong-but-in-range seed and a wrong edge. Now fails closed.
→ **Lesson: "defensive" review findings can be correctness bugs wearing a disguise.**

### The pattern underneath all of them

> **A rewrite that is locally correct but globally wrong, producing output that passes every
> type-level check.**

Which is why the working discipline is **plan → validate the whole plan → apply**, never
apply-then-check. There is no undo: the rewrites mutate instructions
(`ReplaceLastInstrsWithBranch`, an added `pop`), so restoring successors alone leaves blocks
inconsistent.

---

## 3. The gate hierarchy — and what each gate is blind to

Run in this order. Each catches something the one above cannot.

| # | gate | catches | **blind to** |
|---|---|---|---|
| 1 | `ilverify`, complete refs | type-unsafe IL | anything type-safe |
| 2 | empty method bodies | deleted live code | code that is wrong, not absent |
| 3 | stack underflow | unbalanced CFG edits | balanced-but-wrong flow |
| 4 | no `ret`/`throw` in body | orphaned exits | exits reachable in CFG but not in execution |
| 5 | **state-machine trace** | mis-resolved dispatch | non-constant transitions |

**Gate 1 caveats.** The reference set must be *complete* — a missing assembly makes `ilverify` skip
methods, which under-counts. Treat every `FileLoadErrorGeneric` as a defect in your reference set, never
as noise. And compare against the original binary: on a newer SDK, ~99% of raw errors are version noise.

**Gate 4 caveat.** A valid exit is `ret` **or** `throw`/`rethrow`. Counting only `ret` reports false
positives on compiler-generated iterator `IEnumerator.Reset()` stubs, whose entire body is
`newobj NotSupportedException; throw`.

**Gate 5 does not exist in de4dot yet.** It is currently implemented downstream as a source-level
detector over the decompiled C#. Porting it in is the highest-value structural item (§5).

**Readability signals** (unresolved dispatch counts, `goto` density, `infLoop`) are *deletion-gameable* —
they improve when code is deleted. Never trust a drop in them without checking gates 1–5.

---

## 4. What is still broken: 17 mis-resolved state machines

The remaining defect class, and the only one standing between here and "functional".

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

**Why every existing gate passes it.** The `ret` is a switch *target*, so it is reachable in the CFG.
The IL is type-safe. Nothing is deleted. Only tracing the state variable reveals it.

**Why it is the worst class.** A mis-resolved machine does not look broken — **it looks like a shorter
method**. Every statement on the unreachable tail silently disappears from the decompiled output. In the
example above the correct chain is `3→2→6→7→5→0`, and `shapeType` is simply absent from the decompile.
This has already caused a real downstream reconstruction error.

**The strongest available clue: `TERMINATES = 0`.** Not one decidable machine in the corpus survives
partial resolution intact — the tally is 0 terminating / 17 looping / 8 undecidable. That is not a
distribution you get from occasional edge mis-resolution; it says partial resolution is *reliably*
destructive.

The reasoning behind it: a machine de4dot resolves **fully** vanishes from the output — the switch
becomes unreachable, dead-block removal takes it, and straight-line code is left behind. So every
machine this detector can see is one de4dot did *not* fully resolve, and the population splits into
exactly two buckets: left completely alone (non-constant transitions ⇒ UNKNOWN, 8 of them, faithful)
and partially resolved (constant transitions ⇒ **all 17 broken**). The empty middle is the finding.

**This should change the fix strategy.** "Find the one edge that resolves wrongly" presumes a mostly-
correct pass with a bug in it. The distribution says otherwise: any dispatch left half-resolved is
wrong, so the safe primitive is **all-or-nothing per machine** — resolve every case or none — rather
than a better per-edge decision. That makes the §5-item-2 "fallback" the *primary* candidate, and
demotes the per-edge seed investigation below to a way of understanding *why*, not the fix itself.

**Localisation so far** (three hypotheses refuted to get here):
- Not the predecessor-redirect path. Gates were built into both the Reactor-specific rewriter and the
  shared switch pass; both fired on real rewrites, neither changed the count. Reverted.
- Not switch-table corruption. Instrumenting `switchBlock.Targets` before and after every pass showed
  all targets stay distinct throughout.
- **Current hypothesis:** XorSwitch resolves an affine *state-transform* block
  (`next = (state * A) ^ B`) by stripping it and leaving a fixed destination on the fall-through. But a
  transform block's destination is a **function of the incoming state**. Collapsing it to one fixed
  target is only sound when exactly one state reaches it.
- **Next experiment:** log, per resolved transform block, the number of distinct incoming seeds
  `EdgeResolver` saw. More than one ⇒ confirmed, and the fix is to refuse to collapse it (leave that
  case unresolved) rather than pick a winner.

**Fallback if that is wrong:** all-or-nothing per machine, gated on the traced path terminating. That is
the previously-failed Exp 2 idea with a real gate instead of `FailedCount == 0` (which lies —
passthrough blocks are marked resolved without being rewired) and **without** its dead-switch removal
(which deleted live code). Unresolved-but-faithful is an acceptable outcome; wrong is not.

---

## 5. Roadmap to "fully functional"

Ordered by value. Each step must hold every gate in §3.

1. **Port the state-trace into de4dot as gate 5.** Until this is in-tree, this bug class is invisible to
   anyone working on the deobfuscator. It should refuse to keep a resolution whose machine does not
   terminate. Highest value: it converts an unknown into a regression test.
2. **Fix the 17 (§4).** Target: 0 mis-resolved machines, with the affected methods moving to
   *unresolved-but-faithful*, **not** to "resolved".
3. **Two-variable chained dispatch** (`WORKLOG.md` #5). An outer plain `switch` wrapped around the inner
   affine one; only the inner layer is recognised. 8 sites in the corpus, currently left fully
   unresolved and therefore *correct*. This is a **readability** item, not a correctness one — which is
   why it ranks below the above despite being the oldest open task. Three attempts have failed; all
   three are documented in `IMPROVEMENT_PLAN.md` and must not be re-tried blind. The plan/apply split
   now in place is the scaffold the real attempt (Exp 4) always needed.
4. **Opaque-predicate folding with a zero-seeded local.** A common Reactor shape is
   `switch((num = (num*A)^B) % k)` where `num` is an un-stored, `.locals init`-zeroed local. de4dot
   already models this (`cached_zeroed_locals`) but only uses it when emulating from the method's first
   instruction, so loops inside an `if` never fold. Generalising the condition — *no block that can
   reach the dispatch writes the local, therefore the zero still holds* — should fold a large share of
   the remaining unresolved dispatches. Readability, low risk.
5. **Closure/lambda inlining.** Nested `<>c__DisplayClass` closures are not recursively inlined.
   Readability.

### Definition of done

- Gates 1–5 all zero across the corpus.
- Every remaining unresolved dispatch is *faithful* — verified by the state trace, not assumed.
- No pass can produce output that passes gates 1–4 while being semantically wrong; i.e. gate 5 is
  in-tree and enforced, not a downstream check.

---

## 6. Working notes worth not rediscovering

- **An offset is not a block identity.** Instructions de4dot synthesises default to `Offset == 0`, so
  distinct rewritten blocks all print as `IL_0000`. A diagnostic keyed on offsets alone produced a
  confidently wrong conclusion here. Use a positional index alongside.
- **Bisect with the CLI before reading code.** `--no-cflow-deob`, `--dont-rename` and
  `--dr4-types false` each isolate a subsystem in one run, and settled three separate questions faster
  than any amount of static reading.
- **Then bisect *within* cflow by disabling one `IBlocksDeobfuscator` at a time** — that is what
  distinguished the Reactor-specific pass from the shared one.
- **Instrument, do not guess.** Every hypothesis resolved here was resolved by a print statement. Two
  that were resolved by reasoning alone were later refuted.
- **A failed fix is a result.** Two gates were built, measured, found to change nothing, and reverted.
  Reverting is correct: an unvalidated guard with a measurable readability cost does not belong in a
  pass with three failed experiments behind it. The *designs* are preserved in the theory notes so they
  are not re-derived.
- **`realBug 0` is not "correct".** It means no type-unsafe IL. Say that precisely; the difference is
  exactly the 17 methods in §4.
