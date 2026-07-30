# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

de4dot is a .NET deobfuscator and unpacker (GPLv3). It detects which obfuscator was used, then applies obfuscator-specific and generic deobfuscation passes (string decryption, control flow restoration, proxy method removal, symbol renaming, etc.). It uses [dnlib](https://github.com/0xd4d/dnlib/) for reading and writing .NET assemblies.

## Build Commands

Solution filters, one per target framework (`de4dot.slnx` is the full solution):

```bash
# .NET (primary development target — net10.0 since the upstream multitargeting change)
dotnet build -c Release de4dot.net.slnf

# .NET Framework 4.8
dotnet build -c Release -f net48 de4dot.netframework.slnf

# Full release build (both targets, via build.ps1)
pwsh build.ps1

# Run IL-based inlining tests (requires ilasm/ildasm on PATH)
pwsh test.ps1
```

There is no unit test project (xUnit/NUnit/MSTest). The only automated tests are IL-based integration tests in `tests/samples/inlining/` that assemble IL, run de4dot on it, then disassemble the output.

## Architecture

### Project dependency graph

```
de4dot (exe) → de4dot.cui → de4dot.code → de4dot.blocks
                                         → de4dot.mdecrypt
                                         → AssemblyData
de4dot.mcp (MCP server) → de4dot.cui
```

### Key projects

- **de4dot.blocks** — IL basic block representation (`Block`, `Blocks`, `MethodBlocks`) and control-flow deobfuscation (`cflow/`). Foundation layer; has no project references, only depends on dnlib.
- **de4dot.code** — Core deobfuscation logic: the `ObfuscatedFile` pipeline, all obfuscator-specific deobfuscators under `deobfuscators/`, the symbol `renamer/`, string inlining, and assembly client infrastructure for dynamic decryption.
- **de4dot.cui** — CLI entry point. `FilesDeobfuscator` orchestrates the full pipeline: detect → deobfuscate → rename → save. `Program.cs` registers all `IDeobfuscatorInfo` implementations.
- **de4dot.mdecrypt** — Method decryption helpers.
- **AssemblyData** — Runs in a separate process to dynamically invoke string decrypter methods in the target assembly (delegate-based or emulated). Communicates via remoting/.NET hosting.
- **de4dot.mcp** — Model Context Protocol (MCP) server exposing deobfuscation as tools/resources for AI agents.

### Deobfuscation pipeline

1. **Detection** — Each `IDeobfuscatorInfo` creates an `IDeobfuscator` that is `Initialize`d with the module and calls `Detect()` returning a confidence score. Highest score wins.
2. **Module decryption** — `GetDecryptedModule()` handles packed/encrypted assemblies; if it returns data, the module is reloaded via `ModuleReloaded()`.
3. **Deobfuscation passes** — `DeobfuscateBegin()` → per-method `DeobfuscateMethodBegin/Strings/End` (operating on `Blocks`) → `DeobfuscateEnd()`.
4. **String decryption** — Static (`StaticStringInliner`) or dynamic (`DynamicStringInliner` via `AssemblyData` subprocess). The `StringInlinerBase` replaces call instructions with `ldstr`.
5. **Symbol renaming** — `Renamer` in `de4dot.code/renamer/` renames types/methods/fields/etc. across all assemblies being processed together.
6. **Save** — Module written back via dnlib's `ModuleWriter`/`NativeModuleWriter`.

### Adding a new deobfuscator

Each obfuscator lives in `de4dot.code/deobfuscators/<Name>/` and needs:
1. `DeobfuscatorInfo` class implementing `IDeobfuscatorInfo` (factory + options)
2. `Deobfuscator` class extending `DeobfuscatorBase` (detection + deobfuscation logic)
3. Registration in `de4dot.cui/Program.cs` → `CreateDeobfuscatorInfos()`

Deobfuscators can also be loaded as plugins from a `bin/` directory adjacent to the executable via reflection (any DLL exporting `IDeobfuscatorInfo`).

### Shared build configuration

`Directory.Build.props` sets language version, signing, and output path for all projects; each csproj
declares its own `TargetFrameworks` (net48;net10.0). It replaced `De4DotCommon.props` in the upstream
multitargeting change. Note the fork disables `SignAssembly` on non-Windows: strong-name signing uses a
SHA1 digest that OpenSSL rejects where SHA1 signatures are disabled, which kills the build outright.

**One project is deliberately pinned to net8.0: `de4dot.constdata`.** Reactor's constant/string
decrypter obtains its data array by loading the obfuscated assembly and running its `.cctor`, and
.NET 10's loader rejects Reactor metadata with `BadImageFormatException: Enclosing type(s) not found`
— which silently leaves every constant and string encrypted. That load happens in the
`de4dot.constdata` worker process, pinned to net8.0 so the host can target net10.0 freely. Raising the
worker's target reintroduces the bug; raising the host's does not. Full history, the measured numbers,
and the fail-closed invariant the worker must preserve: `ROADMAP.md` §8.

`Directory.Packages.props` manages centralized NuGet package versions (dnlib 4.5.0 — a fork delta;
upstream is on 3.6.0).

## Documentation rules

Two root-level documents, and the split is strict:

- **`ROADMAP.md`** — the only narrative document. Measured state, the correctness metric, every root
  cause, the gate hierarchy, failed experiments, the ordered path to done.
- **`WORKLOG.md`** — a bare checkbox queue. One line per task, pointing at a ROADMAP section.

**Every fact has exactly one home, and you link to it rather than restating it.** This is not a style
preference. These two files were merged out of three (`ROADMAP` + `IMPROVEMENT_PLAN` + `WORKLOG`) that
each restated the correctness metric, the corrected baseline, and the same root causes. The copies
drifted until they contradicted each other — the roadmap simultaneously claimed 0 and 7 broken state
machines, and said gate 5 both did and did not exist in-tree — and by then there was no way to tell
which copy was right without re-measuring from the binaries. The build commands in a skill file named
a solution file that had not existed for two framework versions. A second copy of a fact is a
liability, not redundancy: it cannot be kept in sync, and its existence destroys your ability to trust
either copy.

Concretely, when writing docs here:

- Adding "just a brief summary" of something already documented elsewhere is the failure mode. Link.
- Numbers (baselines, corpus counts, gate results) belong wherever the gate that produces them is
  defined, never inline in a second document.
- If a section has gone stale, fix it or delete it. Leaving it and writing the correct version
  somewhere else is what produced every contradiction above.
- Prefer deleting a paragraph to hedging it. A doc nobody trusts costs more than a doc that is short.

**This repository must read as fully self-contained.** de4dot is a general-purpose .NET
deobfuscator, and its correctness argument has to stand on its own. In docs, skills, code comments and
commit messages, do not name or allude to any other repository, organisation, or piece of work that
happens to supply test material — not even indirectly ("the consumer", "the project using this",
"downstream"). Nothing here should imply that such a thing exists.

Practical form of the rule:

- Corpus assemblies are `S1`, `S2`, `S3`. Never their real filenames, product names, or vendor.
- Describe external tooling by what it measures, never by its name or path.
- Illustrative identifiers must be de4dot-generated or invented. Do not paste a type name, string
  literal or API name lifted from a target assembly if it would identify what that assembly is.
- The metric is *target*-internal types, meaning types defined in the assembly under deobfuscation.
  Do not characterise the targets beyond that.

## Key dnlib types used throughout

- `ModuleDefMD` — the loaded .NET module
- `TypeDef`, `MethodDef`, `FieldDef` — metadata definitions
- `Instruction` / `OpCodes` — IL instructions
- `IPEImage` — PE file access for native unpacking
