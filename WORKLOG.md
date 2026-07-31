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

- [x] **5.** Two-variable chained dispatch — **DONE.** Slice 1 (walk the machine) 19 → 5, slice 2
  (specialise the region) 5 → **0**. Export reviewed method-by-method against the original binary;
  6 IL methods, all faithful. ROADMAP §7 item 3.

- [x] **12.** Extraction-worker follow-ups — **DONE.** Field token validated in `ReadRequest` as a
  protocol failure; stderr drained via `BeginErrorReadLine` into a bounded buffer and logged on
  failure. Fail-closed boundary untouched; gates byte-identical. ROADMAP §8.

- [x] **13.** v6 branch audit notes — **AUDITED AND CLASSIFIED, neither fixed.** `IDeobfuscator`'s
  added member: **accepted compatibility break** (no compatibility promise to honour, and a default
  interface method is unavailable while net48 is a target). `TrackedArrayValue` mutability:
  **documented invariant**, no reachable hazard — it cannot outlive one `Initialize`. ROADMAP §9.

- [x] **19.** Costura extraction fixture — **DONE, and it found two defects.** The dumper was wired
  into `DeobfuscateEnd`, where the output path it writes relative to is not set up, so every
  extraction threw; and one bad payload aborted the whole run. Moved to `DeobfuscateBegin` (where v3
  does the same thing) and made per-file failures skip. `costura_host` covers compressed,
  uncompressed, a skipped `.pdb` and a declined non-PE.

- [x] **16.** Dispatch whose exit case no state selects, hidden in *undecidable* — **CONTAINED.**
  `StateMachineTracer` now over-approximates a configuration set instead of walking one path, so it
  proves `Loops` where it used to give up; undecidable 16/14/38 → 1/1/2. The existing
  `SelectDispatchCandidate` rejects both bad candidates with no new guard. The other verdict is
  `ExitReachable`, not `Terminates` — the absence of a non-termination proof, never a termination
  proof. ROADMAP §7a.

- [x] **14.** `CflowConstantsInliner` folded constants on an unchecked premise — **DONE.** It now
  refuses unless the declaring type's `.cctor` calls the initialiser, which is what makes the stores
  precede every folded read. Corpus unaffected (all three already had that shape); the refusal path
  is covered by fixtures, since the corpus cannot reach it. ROADMAP §7 item 3.

- [x] **15.** Premise check vetoed selection instead of steering it — **DONE.** `Find()` now applies
  it per candidate and moves on, so a later qualifying candidate is still folded. The check shrank to
  one `.cctor` body scan, which is what makes it affordable per candidate. ROADMAP §7 item 3.

- [x] **17a.** Per-case attribution crossing dispatch boundaries — **FIXED.** The BFS stops at another
  dispatch, both exits attribute correctly, S3 `InterruptQueue` resolves and leaves the rejection set.
  Costs some resolution (dispatch sites 44 → 47, `goto` 84 → 129), the accepted trade. ROADMAP §7a.

- [x] **18.** Review the undecidable set the way #16's two were — **DONE, all faithful.** Diffed by
  identity, which mattered: the count moved 2 → 4 while *three* arrived (`NewIdentifier`,
  `DisableAnnotation`, `CustomizeVisitor`) and `InterruptQueue` left. Each is a genuine source-level
  switch or a machine that exits normally, inside the obfuscator's `while (true)` shell — so the size
  of that set is not a defect count. The `goto` rise is two methods (`SetupRef`, `ConnectRequest`),
  both keeping every call target, so no payload was lost; `while (true)` fell 52 → 49. ROADMAP §7a.

- [x] **19.** Remove Reactor's dead opaque-predicate pairs — **DONE.** `OpaquePredicateRemover`, gated
  on the field never being written and every reader going away too. Drops 47/47/54 field+method pairs
  across S1/S2/S3 with no other measurement moving; gates 1/5/6/7 pass. The trap it cost a rewrite to
  find: removal is queued, not immediate, so readers must be checked against `GetMethodsToRemove()`.
  Bisect lever `DE4DOT_NO_OPAQUE_PREDICATES=1`. ROADMAP §7b.

## Open

- [ ] **17.** Make phase 3's seed fallback sound. Attribution is fixed — the per-case BFS no longer
  expands through another dispatch — and S3 resolves correctly. S1 still emits a machine that cannot
  terminate: when a predecessor has no owning case the fallback takes the first seed that resolves to
  any in-range case, which is a guess, and the edge to the exit is never derived. Its bookkeeping is
  now honest — and sharper than first measured: a seed choice only guesses when candidates disagree, so
  S1's count is **16**, not 61. Declining just those 16 was then **built, measured, reverted** too
  (`goto` 129→1569, nearly the wholesale cost): resolved edges feed `allSeeds`, so withdrawal cascades.
  **No withdrawal strategy works** — the fallback must derive or verify, and must keep feeding
  `allSeeds`. Making it decline instead was built, measured and reverted (`goto` 129 → 1772):
  the guess is load-bearing corpus-wide, so it has to become *sound*, not go away. #16's containment
  catches the bad candidate meanwhile. ROADMAP §7a, and §5 before writing any seed rule.

Add new work here rather than reopening a closed item; if a closed finding turns out to be
wrong, correct the ROADMAP section that owns it and open a fresh entry pointing at it.
