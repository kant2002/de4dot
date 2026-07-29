---
name: hardening-the-shared-cflow-emulator
description: Correctness checklist for de4dot.blocks' shared IL value-emulation code (Int32Value, Int64Value, InstructionEmulator, TypesRestorer) — a class of bug here silently corrupts every deobfuscator that emulates IL, not just the one you're testing against. Use before and after any change to de4dot.blocks/cflow/*.cs or TypesRestorer.cs.
---

# Hardening the Shared cflow Emulator

## When to use

- Changing anything in `de4dot.blocks/cflow/` (`Value.cs`, `Int32Value.cs`, `Int64Value.cs`,
  `InstructionEmulator.cs`, `BranchEmulator.cs`, `ValueStack.cs`, `Real8Value.cs`).
- Changing `de4dot.code/deobfuscators/TypesRestorer.cs`.
- Reviewing a change that touches either, from yourself or someone else.

## Why this code is special

`de4dot.blocks` has **no project references besides dnlib** — it's the foundation layer every
deobfuscator builds on. A correctness bug in the shared IL-value emulator or in `TypesRestorer`
doesn't just affect the obfuscator you happened to be testing against when you found it — it
silently affects **every** deobfuscator that emulates values or restores types, including ones
nobody is actively testing in the same session. Two confirmed past incidents:

### Shift-overflow guard regression (`Int32Value`/`Int64Value`)

`Shl`/`Shr`/`Shr_Un` need an out-of-range guard: a shift count that is a nonzero multiple of the
operand width (32 or 64 — C# masks the shift count at that width, so e.g. shifting a 32-bit value by
32 is a no-op in real semantics, not a full clear) must **not** be treated as producing a fully-known
result. A version of this code computed `wordbits - shift == 0` for such counts and derived an
"all-bits-valid mask," which turned a genuinely **unknown** operand into a **known constant 0**.
Obfuscators emit oversized/edge-case shift counts deliberately (as an anti-analysis trick and as a
side effect of arithmetic obfuscation), so this isn't a theoretical edge case — it's actively
triggered by real obfuscated code, and it corrupts every deobfuscator that emulates shifts. Fix:
restore the explicit guard; treat any shift count that is a nonzero multiple of the operand width as
producing an **unknown** value, never a computed one.

### `TypesRestorer` narrowing on partial write information

`TypesRestorer` infers a concrete type for `object`-typed fields/arguments from the set of values
written to them. A version of this silently **ignored** writes whose value type it couldn't
determine (e.g. a boxed value type it didn't track) instead of treating the field as still-ambiguous.
The result: a field that is genuinely `object` (written from multiple incompatible sources) got
narrowed to the single write type it *could* identify (commonly `string`), because the untyped
writes were invisible to the narrowing decision — breaking every other write site with the new,
too-narrow type. Fix: track a `hasUnknownWrite` flag per field/argument; **never narrow if any write
site's value type is unknown**, full stop — an ambiguous field must stay `object`.

## The general pattern behind both bugs

Both bugs share a shape: **treating "I don't currently know how to compute this" as equivalent to "I
computed a specific value/type."** In emulation/inference code, unknown must be a distinct,
sticky state — once a value or a field's writes include *any* unknown contribution, the result must
stay unknown/unnarrowed, never fall back to whatever partial information happened to be available.
When reviewing or writing code in this layer, explicitly ask: *does every code path that fails to
fully determine a value correctly propagate "unknown," or does it silently fall through to a
default that looks like a real answer?*

## Workflow for any change here

1. Make the change.
2. Run the full correctness methodology from
   the `measuring-deobfuscation-correctness-with-ilverify` skill against **every** obfuscator in your test
   corpus that exercises emulation/type-restoration — not just the one that motivated the change.
   A shared-layer fix or regression shows up (or hides) in obfuscator-specific behavior you weren't
   looking at.
3. If introducing new emulated operations (new opcodes, new value-kinds), explicitly enumerate their
   overflow/edge-case behavior (shift-by-width-multiple, divide-by-zero, unbox-of-unknown-type,
   sign-extension boundaries) rather than only implementing the common case.

## Common scenarios

**Scenario: adding emulation support for a new IL opcode.** Before writing the "happy path" compute
logic, write down every input condition under which the result is *not* fully knowable, and make
sure the implementation returns unknown in exactly those cases — don't discover the edge cases via a
regression report later.

**Scenario: a `TypesRestorer`-adjacent change needs to type a field/argument from its writes.** Check
whether every write site's value type is actually determinable before narrowing anything — a single
untyped write anywhere in the set should block narrowing entirely, not just get skipped.

## Pitfalls

- Don't validate a shared-layer change only against the obfuscator you're actively iterating on;
  run the full corpus (see the `measuring-deobfuscation-correctness-with-ilverify` skill).
- Don't treat "this write's type isn't trackable yet" as "this write doesn't count" — that's the
  exact mistake that caused the `TypesRestorer` bug.
- Don't assume a shift/arithmetic edge case is unrealistic input — obfuscators specifically target
  these edges, so "real code would never do this" is not a valid reason to skip a guard here.
