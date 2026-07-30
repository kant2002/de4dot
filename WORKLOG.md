# de4dot .NET Reactor — Improvement Queue

**This file is a queue, not a record.** One line per task, status plus a pointer into
[`ROADMAP.md`](ROADMAP.md), which is the only place root causes, measurements, failed experiments and
the correctness metric are written down. Resist explaining anything here: a second copy of a finding
is how the three documents this was merged out of came to contradict each other.

Corpus: **S1**, **S2** (smaller), **S3** (large) — three .NET Reactor v6.x assemblies, not in this repo.

Gating rule for every change: `realBug` 0/0/0, no empty bodies, no stack underflows, no method left
without `ret`/`throw`/`rethrow`. Definitions and the two ways to measure it wrong: ROADMAP §2.
Run order and each gate's blind spot: ROADMAP §4.

## Done

- [x] **1.** Shift-guard emulator regression (shared) — ROADMAP §3 #1
- [x] **2.** `TypesRestorer` field/arg mistyping on partial write info (shared) — ROADMAP §3 #2
- [x] **3.** `DisplayClassCleaner` hardening — ROADMAP §3 #3
- [x] **4.** Reflection-proxy type confusion, `realBug` 17/17/1 → 2/2/1 — ROADMAP §3 #4
- [x] **4b.** `MissingMethod` dangling refs, 2/2/1 → 0/0/1 — ROADMAP §3 #4b
- [x] **4c.** `DelegateCtor` on a delegate-pinned method, 0/0/1 → 0/0/0 — ROADMAP §3 #4c
- [x] **4d.** Passes could emit methods that never terminate, 21 → 0 — ROADMAP §3 #4d
- [x] **4e.** Opaque-predicate folding with a zero-seeded local — **built, measured, reverted.** The
  reverted work *is* the result; the premise is false. ROADMAP §7 item 4 before touching this again.
- [x] **6.** Review findings incl. `FindSimplePath` ambiguity (a correctness fix, not cosmetic) —
  ROADMAP §3 #6
- [x] **7.** Gate 5 (`StateMachineTracer`) in-tree — ROADMAP §4
- [x] **8.** Constant-data extraction worker; fork-wide net8.0 pin removed — ROADMAP §8
- [x] **9.** XorSwitch branch-and-select: non-terminating machines 19 → 0 — ROADMAP §6

- [x] **10.** ~~Closure/lambda inlining for nested `<>c__DisplayClass`.~~ **DONE for the actionable
  part, CLOSED for the rest.** Measured: nesting was never the dominant cause. Obfuscator residue was
  (79 closure types / 100 construction sites), and `DisplayClassCleaner` now removes it — −274 fields,
  every gate unchanged. What is left is a decompiler limitation over 15 types. ROADMAP §7 item 5.

- [x] **11.** ~~Resolve the machines contained by branch-and-select by fixing the `EdgeResolver`
  double-apply.~~ **CLOSED — not separately actionable.** The seed is wrong because the case
  attribution is, and correcting the attribution cannot solve these methods: the state they need is
  relational (inner state **plus** outer-dispatch arm) and `DispatchNode` cannot represent it. Folded
  into **#5**. ROADMAP §5 — read it before writing any new attribution rule or seed guard.

## Open

- [ ] **5.** Two-variable chained dispatch — deferred, and it is a **representation change**, not a
  seeding fix: chained dispatch context, shared transform blocks, stack-carried inputs. Needs joint
  inner+outer resolution, explicit stack rebalancing, and per-method re-verification gating. Three
  prior attempts all produced invalid IL; **read ROADMAP §7 item 3 and §5 before starting** rather
  than re-deriving them.
- [ ] **12.** Extraction-worker follow-ups: validate the field token before `Module.ResolveField`;
  drain worker stderr asynchronously. Neither touches the fail-closed boundary. ROADMAP §8.
- [ ] **13.** Low priority / latent, from the v6 branch audit: `IDeobfuscator`'s added member is a
  breaking change for out-of-tree plugins; `TrackedArrayValue` is a mutable `Value`. ROADMAP §9.
