---
name: adding-a-deobfuscator-module
description: The structural pattern for adding a new obfuscator-specific deobfuscator to de4dot, or extending an existing one — the IDeobfuscatorInfo/DeobfuscatorBase split, registration, the pipeline hooks each deobfuscator implements, and where shared vs. obfuscator-specific logic belongs. Use when adding support for a new obfuscator/packer or adding a new pass to an existing per-obfuscator deobfuscators folder.
---

# Adding or Extending a Deobfuscator Module

## When to use

- Adding support for a new obfuscator/packer that isn't currently handled.
- Adding a new deobfuscation pass (e.g. a new string/constant decryption variant, a new control-flow
  pattern) to an existing `de4dot.code/deobfuscators/<Name>/` folder.
- Deciding whether new logic belongs in an obfuscator-specific folder vs. shared code in
  `de4dot.blocks` or `de4dot.code/deobfuscators/*.cs` (the top-level shared helpers).

## Project layout recap

```
de4dot.blocks       IL basic-block representation + cflow emulation (Block, Blocks, cflow/*).
                    No project references besides dnlib — foundation layer, shared by everything.
de4dot.code         ObfuscatedFile pipeline, deobfuscators/<Name>/, renamer/, string inlining.
de4dot.cui          CLI entry point; FilesDeobfuscator orchestrates detect -> deobfuscate -> rename -> save;
                    Program.cs registers every IDeobfuscatorInfo.
de4dot.mdecrypt     Method decryption helpers.
AssemblyData        Separate-process dynamic invocation of target string-decrypter methods.
```

## The two required classes

Each obfuscator lives in `de4dot.code/deobfuscators/<Name>/` and needs:

1. **`<Name>Info` implementing `IDeobfuscatorInfo`** — factory + CLI options for this deobfuscator.
   This is what gets registered.
2. **`Deobfuscator` extending `DeobfuscatorBase`** — detection + the actual deobfuscation logic.

Register the `Info` class in `de4dot.cui/Program.cs` → `CreateDeobfuscatorInfos()`. (Deobfuscators
can alternatively be loaded as plugins from a `bin/` directory next to the executable via reflection
— any DLL exporting `IDeobfuscatorInfo` — useful for out-of-tree experimentation without touching
`Program.cs`, but note this makes `IDeobfuscator` interface changes a breaking change for such
plugins; check current interface members before adding a new one lightly.)

## The pipeline your Deobfuscator plugs into

1. **Detection** — `Detect()` returns a confidence score; the `IDeobfuscator` with the highest score
   across all registered infos wins for a given module. Make detection specific enough to not
   false-positive against unrelated obfuscators/plain assemblies.
2. **Module decryption** — `GetDecryptedModule()` for packed/self-decrypting assemblies; if it
   returns data, the module reloads via `ModuleReloaded()`.
3. **Deobfuscation passes** — `DeobfuscateBegin()` → per-method `DeobfuscateMethodBegin/Strings/End`
   (operating on `Blocks`, the basic-block IR) → `DeobfuscateEnd()`.
4. **String decryption** — either `StaticStringInliner` (safe to call the decrypter directly/
   statically) or `DynamicStringInliner` (runs the target's real decrypter method in the
   `AssemblyData` subprocess). `StringInlinerBase` does the actual `call` → `ldstr` replacement.
5. **Symbol renaming** — handled centrally by `Renamer` (`de4dot.code/renamer/`) across all
   assemblies processed together; individual deobfuscators generally don't implement their own
   renaming pass.
6. **Save** — via dnlib's `ModuleWriter`/`NativeModuleWriter`; not something a deobfuscator module
   itself drives.

## Where new logic belongs

- **Obfuscator-specific parsing/pattern-matching** (recognizing this obfuscator's exact IL shapes,
  constant tables, method-name conventions) → the `<Name>/` folder.
- **Anything reusable across obfuscators** (e.g. IL value tracking/emulation, generic proxy-call
  fixing, generic constant readers) → check for an existing shared helper first
  (`de4dot.code/deobfuscators/*.cs` at the top level — `ArrayFinder.cs`, `ConstantsReader.cs`,
  `MethodCallRestorerBase.cs`, `ProxyCallFixerBase.cs`, `TypesRestorer.cs`, etc. — and
  `de4dot.blocks/cflow/*` for emulation) before writing a new obfuscator-local version of the same
  logic. Duplicated logic across obfuscator folders is a maintenance trap: a correctness bug found
  in one obfuscator's copy won't get fixed in the others.
- **Any change to a shared helper affects every deobfuscator that uses it** — see
  the `measuring-deobfuscation-correctness-with-ilverify` skill for why this means testing across your full
  corpus, not just the sample that motivated the change, and
  the `hardening-the-shared-cflow-emulator` skill for the specific emulator correctness patterns to watch for.

## Common scenarios

**Scenario: the new obfuscator has its own string-encryption scheme.** Check whether it fits the
existing `StaticStringInliner`/`DynamicStringInliner`/`StringInlinerBase` split before writing a
bespoke decryption path — most schemes reduce to "compute an offset/key, read from a data blob,"
which the existing generic constant/string decryption helpers may already cover with different
constants.

**Scenario: the new obfuscator has switch/state-machine control-flow obfuscation similar to another
already-supported obfuscator.** Look at how the existing one structures its resolver (detection →
edge/state resolution → rewriting) before designing a new architecture from scratch — see
the `debugging-xorswitch-control-flow-recovery` skill for the shape one such resolver takes and the
correctness pitfalls specific to CFG rewriting (self-loops, double-applied state updates, stack
imbalance after edge cuts).

## Pitfalls

- Don't skip registering the new `Info` class in `Program.cs` and then wonder why the deobfuscator
  never activates.
- Don't make `Detect()` too permissive — a false-positive detection can cause the wrong
  deobfuscator's passes to run against a module, corrupting it in ways that are hard to trace back
  to "wrong detector picked."
- Don't add a member to `IDeobfuscator` without checking downstream impact — it's a breaking change
  for any out-of-tree plugin implementing the interface directly (internal deobfuscators are
  insulated via a virtual default, but external ones may not be).
