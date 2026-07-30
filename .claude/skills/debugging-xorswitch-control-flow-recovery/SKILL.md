---
name: debugging-xorswitch-control-flow-recovery
description: Working on the .NET Reactor v4 XorSwitch control-flow resolver (de4dot.code/deobfuscators/dotNET_Reactor/v4/xorswitch/) — its pipeline stages, the specific correctness bugs already found and fixed there, and why every attempt at joint two-variable dispatch resolution so far has produced invalid IL. Use before touching DispatchDetector, StateUpdateFinder, EdgeResolver, SwitchRewriter, or OpaquePredicateFixer.
---

# Debugging XorSwitch Control-Flow Recovery

## When to use

- Modifying anything under `de4dot.code/deobfuscators/dotNET_Reactor/v4/xorswitch/`.
- Investigating an unresolved or incorrectly-resolved switch/XOR dispatch in a .NET Reactor v4/v6
  sample.
- About to attempt joint resolution of nested (two-variable/chained) dispatch — read the "three
  failed experiments" section first; this has been tried and failed three separate ways already.

## Pipeline shape

```
RelationalDispatchResolver — chained MULTI-SITE machines: walks the whole machine forward and
                             either resolves it entirely or changes nothing. Runs FIRST.
DispatchDetector      — finds candidate affine dispatch sites (state var + switch)
StateUpdateFinder     — locates the state-update expression at the end of a case body
EdgeResolver          — resolves case -> next-state edges by emulating the affine chain
SwitchRewriter        — rewrites resolved edges into direct branches / removes the switch
OpaquePredicateFixer  — handles opaque-predicate variants of the same pattern
XorSwitchTrace        — diagnostic only; DE4DOT_XORSWITCH_TRACE=<substring of a method's full
                        name> logs every state seed with its provenance. Needs de4dot's -v.
```

**The two resolvers answer different questions and must not be conflated.** `EdgeResolver` derives a
seed for one dispatch from case attribution; `RelationalDispatchResolver` derives nothing — it carries
the configuration (pending stack values in order, every local, which dispatch is next) along the edge
as it interprets the method forward from its first instruction. When a machine spans two dispatches
the second is the only one that can be right, because the state a predecessor is entered with depends
on which arm of the *other* dispatch fired. See ROADMAP §5.

The core transform being reversed: a state variable driving a `switch`, where the next-state value
is produced by `(state * mul) ^ xor` (int32-overflow arithmetic) before the next dispatch — the same
affine-transform family used for this obfuscator's generic string/constant decryption
(`GenericConstantDecrypter`/`GenericConstantInliner`), just applied to control flow instead of data.

## Fixed correctness bugs (read these before assuming a bug is new)

- **`SwitchRewriter` self-loop guard.** Never redirect a block to itself — a basic block has no
  internal branch, so `payload; br self` is an infinite loop regardless of what payload is retained.
  Leaving the edge unresolved (a recoverable `goto`) is strictly better than closing a self-loop.
- **`EdgeResolver` phase-6 double-apply.** The forward stateVar trace yields the value *at the
  predecessor's entry*. A prior version re-emulated the backward chain in `TryResolveEdge` and
  applied the predecessor chain's affine update a **second time**, producing a wrong-but-in-range
  case index → silently wrong control flow (not a crash — this is the dangerous kind of bug). Fixed
  via a `seedIsAtPredecessorEntry` flag that emulates only the predecessor, not predecessor+self.

Both are exactly the kind of bug this pass is prone to: **plausible-looking but wrong** rewrites,
not crashes. This is why the `measuring-deobfuscation-correctness-with-ilverify` skill's introduced-bug
count is the only trustworthy signal here — a method that "resolved cleanly" by this pass's own
bookkeeping can still be wrong.

## Two-variable (nested) dispatch: slice 1 has landed — read this before touching it

`RelationalDispatchResolver` resolves the subset needing no block duplication (every transition
determined, no payload block entered twice). Branch-and-select rejections went **19 → 5 with zero new
rejections**; ROADMAP §7 item 3 has the measurements, the acceptance rule (diff the *set* of rejected
method identities, never the count) and the three defects found building it. Slice 2 — specialising a
payload block entered in two configurations — is open.

The four attempts below all predate it and all tried to rewrite edges in place rather than generate
from an explored walk. They are kept because their failure modes are still live hazards for slice 2.

## Four failed attempts at two-variable (nested) dispatch — do not repeat these

Some methods nest an **outer plain-int `switch(state)`** around the **inner affine xor-switch**.
Only the inner layer is currently recognized. Three joint-resolution attempts, all reverted:

1. **Plain-header detection.** Correctly recovered the state machine structure (one representative
   method decoded to its exact linear form) and sharply dropped unresolved-dispatch/loop counts —
   **but** left the bare `switch` block with no stack input, producing ~592 stack underflows.
   Readability metrics looked great; IL was badly broken.
2. **All-or-nothing + atomic dead-switch removal.** Metrics looked good again — **but** it was
   silently **deleting live method bodies**. The lesson: a pass's own `FailedCount == 0` is *not* a
   reliable "fully resolved" signal — passthrough blocks can be marked resolved without actually
   being rewired, and a naive cleanup step will then delete them as "dead."
3. **Edge rewrites only, no explicit block removal (reachability-based cleanup).** Avoided the
   deletion and underflow failure modes of the first two — **but** introduced new invalid-IL errors
   (stack-depth/return-with-value-on-stack class) because the plain state-update push isn't cut when
   an edge is rewritten, leaving the evaluation stack unbalanced.

**The actual fix (not yet attempted)** needs all of: joint inner+outer resolution, **explicit stack
rebalancing on every CFG edit** (the full state-update expression, including its push, must be cut —
not just the branch target), handling of conditional/opaque state updates, and **gating each rewrite
by per-method re-verification** — keep a rewrite only if the method still verifies afterward, rather
than trusting the pass's internal bookkeeping. Treat the current introduced-bug baseline as a floor:
any new attempt that raises it, even temporarily "for now," should be reverted, not merged with a
TODO.

## The other dispatch shape: zeroed-local opaque predicates — one failed attempt

`switch((num = (num*A)^B) % k)`, where `num` reads as an un-stored `.locals init`-zeroed local. 37
sites in the corpus. Seeding the emulator with that zero at a non-entry block was built, measured and
reverted; the full record is in `ROADMAP.md` §7 item 4. Two things
to know before touching it:

- **A single fold can only ever resolve the sites whose machine exits on its first iteration** — one
  of 37 here. The rest run 2–3 junk iterations first, so they need the loop **peeled** (a duplicated
  dispatch block per iteration). A sound one-edge fold correctly refuses them.
- **An initial-value claim is not trustworthy over a partially-resolved body.** In the original, that
  state is often seeded on the *evaluation stack* by an outer machine's case body, with the store and
  the load in two different blocks. After partial resolution those blocks can merge and the seeding
  push is gone, so `ldloc` reads the init zero where the original read a computed state. Acting on it
  deleted a live branch, and every gate passed on the result.

## Open (defensive) review findings — ALL ADDRESSED (WORKLOG #6); kept for the reasoning

- `FindSimplePath` (in `EdgeResolver`) returned the *first* BFS path and did not detect ambiguity,
  despite its doc comment claiming otherwise — a wrong stateVar seed can be silently derived when
  multiple case→predecessor paths differ. Worth an explicit ambiguity check before trusting a
  single-path result in an edge case.
- `FindSimplePath` is invoked twice per `TryEmulateForSeed` — cache it rather than recomputing.
- The "pop TOS, validate case index, read stateVar" result-extraction tail is duplicated across
  multiple methods — a correctness fix applied to one copy needs to be applied to all of them; look
  for all call sites before considering a bug fixed.
- A stale doc comment on `FindSimplePath` references a "30 blocks" limit that no longer matches the
  actual `maxBlocks` constant in code — don't trust doc comments over the literal constant when
  reasoning about search bounds here.

## Common scenarios

**Scenario: a candidate fix makes unresolved-dispatch counts drop noticeably.** Do not treat this as
success on its own — this exact pattern (metrics improve, IL correctness doesn't) is precisely how
all three nested-dispatch experiments looked promising before failing. Run the full introduced-bug
diff from the `measuring-deobfuscation-correctness-with-ilverify` skill before drawing any conclusion.

**Scenario: you're tracing why a specific method's dispatch didn't resolve.** Check first whether it
matches the single-layer pattern (should resolve, investigate as a possible new bug) or the nested
outer-plain/inner-affine pattern (known unsupported class, expected to be left as a recoverable
`goto` rather than mis-resolved).

## Pitfalls

- Never let a CFG rewrite proceed without accounting for what it leaves on the evaluation stack —
  every failure mode above except the self-loop guard traces back to a stack-balance assumption that
  didn't hold.
- Don't trust a pass's own success/failure counters as a verification signal; only external
  `ilverify` re-checking after the rewrite is trustworthy.
- Don't reuse the emulation logic here to "quickly" resolve a similar-looking pattern in a different
  obfuscator's folder without checking whether it's actually shared code already
  (the `adding-a-deobfuscator-module` skill) — duplicating this logic duplicates its bug surface too.
