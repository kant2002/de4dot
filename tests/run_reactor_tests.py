#!/usr/bin/env python3
"""
Run the .NET Reactor IL fixtures: assemble -> de4dot -> check what a pass decided.

Covers two passes, because they share every expensive part -- locating ilasm, forcing the Reactor
deobfuscator, reading the decision back:

  * `RelationalDispatchResolver` -- which dispatch shapes it collapses and which it refuses.
  * `CflowConstantsInliner`      -- whether it folds module constants, which turns on a premise the
                                    corpus can never falsify (there, the premise always holds), so
                                    the refusal path exists only here.

Why this exists alongside test.ps1
----------------------------------
`test.ps1` is Windows-only in practice -- it hardcodes NETFX tool paths and a `win-x64` de4dot -- and
it byte-compares disassembled IL against a checked-in `.cleaned.il`. Neither works here: there is no
pwsh, and a byte comparison against output from a different ildasm build fails for reasons that have
nothing to do with de4dot.

So these fixtures assert a **property** instead of an exact rendering: what
`RelationalDispatchResolver` decided, read from its own trace, plus whether the dispatch survived.
That is what the pass claims, it is stable across toolchain versions, and a fixture that fails says
something specific rather than "the output moved".

Requires ilasm; `--fetch-tools` restores it from NuGet if it is missing, so the next person does not
repeat the archaeology. Nothing here touches the corpus -- the acceptance check for a real change is
still the downstream scorecard.

    python3 tests/run_reactor_tests.py [--fetch-tools] [--keep]
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FIXTURES = ROOT / "tests" / "samples" / "xorswitch"
RID = "win-x64" if sys.platform == "win32" else "linux-x64"
EXE = ".exe" if sys.platform == "win32" else ""

# Pinned so a fixture failure is never "the tool moved". Any version that assembles this IL is fine;
# the assertions do not depend on the assembler's output formatting.
ILASM_PKG = f"runtime.{RID}.Microsoft.NETCore.ILAsm"
ILASM_VER = "9.0.0"


class Expectation:
    """What a fixture must make a pass decide."""

    def __init__(self, name, outcome=None, resolved=None, switch_gone=None, calls=None,
                 source=None, log_contains=(), log_lacks=(), body_lacks=(), il_contains=(),
                 il_lacks=(), blobs=None, files_written=()):
        self.name = name
        self.outcome = outcome            # substring expected in the resolver's outcome line
        self.resolved = resolved          # True: it applied a plan; False: it must not have
        self.switch_gone = switch_gone    # True: no switch left in Target::Run
        self.calls = calls                # exact order of Marker calls left in Target::Run
        # Generated fixtures: a callable returning IL text, for shapes that need ~100 near-identical
        # members. Writing those out by hand would be 900 lines of noise nobody would read, and the
        # .il still lands in the workspace, so --keep leaves it just as inspectable.
        self.source = source
        self.log_contains = log_contains  # substrings de4dot's output must contain
        self.log_lacks = log_lacks        # substrings it must not
        self.body_lacks = body_lacks      # opcodes/text that must be gone from Target::Run
        self.il_contains = il_contains    # text that must survive anywhere in the output module
        self.il_lacks = il_lacks          # text that must not
        # Embedded managed resources. ilasm materialises `.mresource public 'x' { }` by opening a
        # file literally named x, so the blob has to sit next to the .il under its resource name.
        self.blobs = blobs                # callable -> {resource name: bytes}
        self.files_written = files_written  # files de4dot must produce beside its output


# ------------------------------------------------------------------ generated cflow-constants IL

# Above CflowConstantsInliner's >=100 threshold with room to spare, so the fixture is testing the
# premise check rather than sitting on the selection boundary.
CONST_COUNT = 120


def cflow_constants_il(types, reads, helper_for=None) -> str:
    """
    One or more sealed types of static int32 constants, each with the method that stores them, shaped
    exactly as CflowConstantsInliner selects: sealed, >=100 fields, a static assembly-visible
    initialiser whose body is `ldc.i4; stsfld` pairs.

    `types` is a list of (name, cctor_body). A cctor_body of None emits no .cctor at all, which is a
    different refusal from one that exists but calls elsewhere. Whether a type's .cctor reaches its
    initialiser is the whole variable here: it is what decides if folding that type's constants is
    justified, and with several types it also decides which one gets selected.

    `reads` are the fields Target::Run loads, as "Type::fN" -- the sites the fold would rewrite.
    `helper_for` adds a method calling that type's Init() from outside the .cctor chain, the case a
    check counting callers would accept and this one must not.
    """
    blocks = []
    for name, cctor_body in types:
        fields = "\n".join(f"  .field public static int32 f{i}" for i in range(CONST_COUNT))
        stores = "\n".join(f"    ldc.i4 {1000 + i}\n    stsfld int32 {name}::f{i}"
                            for i in range(CONST_COUNT))
        cctor = "" if cctor_body is None else f"""\
  .method private hidebysig specialname rtspecialname static void .cctor() cil managed
  {{
    .maxstack 8
{cctor_body}
    ret
  }}
"""
        blocks.append(f""".class public sealed auto ansi beforefieldinit {name} extends [mscorlib]System.Object
{{
{fields}

  .method assembly hidebysig static void Init() cil managed
  {{
    .maxstack 2
{stores}
    ret
  }}

{cctor}
  .method assembly hidebysig static void Unrelated() cil managed
  {{
    .maxstack 8
    ret
  }}
}}
""")

    loads = "\n".join(f"    ldsfld int32 {r}" for r in reads)
    adds = "\n".join("    add" for _ in range(len(reads) - 1))
    helper = "" if helper_for is None else f"""
  .method public hidebysig static void Helper() cil managed
  {{
    .maxstack 8
    call void {helper_for}::Init()
    ret
  }}
"""
    body = "\n".join(blocks)
    return f""".assembly extern mscorlib {{ .ver 4:0:0:0 }}
.assembly cflow {{ }}
.module cflow.dll

{body}
.class public auto ansi beforefieldinit Target extends [mscorlib]System.Object
{{
  .method public hidebysig static int32 Run() cil managed
  {{
    .maxstack 4
{loads}
{adds}
    ret
  }}
{helper}}}
"""



def costura_host_il() -> str:
    """A Costura-packed host: one plain assembly, one raw-deflated, one .pdb, one non-PE."""
    return """.assembly extern mscorlib { .ver 4:0:0:0 }
.assembly costura_host { }
.module costura_host.dll
.mresource public 'costura.plain.dll' { }
.mresource public 'costura.packed.dll.compressed' { }
.mresource public 'costura.notes.pdb' { }
.mresource public 'costura.bogus.dll' { }
.class public auto ansi abstract sealed beforefieldinit Target
    extends [mscorlib]System.Object
{
  .method public hidebysig static void Run() cil managed { .maxstack 1  ret }
}
"""


def costura_blobs() -> dict:
    """
    The resources the host embeds. The two assemblies are real ones -- a hand-rolled PE header is
    not enough, because what consumes them further down loads them as assemblies.
    """
    import zlib
    payload = _assemble_payload()
    deflated = zlib.compressobj(9, zlib.DEFLATED, -15)          # raw deflate, as Costura writes it
    return {
        "costura.plain.dll": payload,
        "costura.packed.dll.compressed": deflated.compress(payload) + deflated.flush(),
        "costura.notes.pdb": b"not an assembly, and must be skipped rather than written out",
        "costura.bogus.dll": b"named like an assembly, but has no MZ and must not be claimed",
    }


_payload_cache = {}


def _assemble_payload() -> bytes:
    if "bytes" not in _payload_cache:
        ilasm = find_ilasm(False)
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            (tmp / "p.il").write_text(
                ".assembly extern mscorlib { .ver 4:0:0:0 }\n.assembly payload { }\n"
                ".module payload.dll\n"
                ".class public auto ansi abstract sealed P extends [mscorlib]System.Object\n"
                "{ .method public hidebysig static void Go() cil managed { .maxstack 1  ret } }\n")
            flag = "/" if sys.platform == "win32" else "-"
            subprocess.run([str(ilasm), f"{flag}dll", f"{flag}quiet", str(tmp / "p.il"),
                            f"{flag}output={tmp / 'payload.dll'}"], capture_output=True, check=True)
            _payload_cache["bytes"] = (tmp / "payload.dll").read_bytes()
    return _payload_cache["bytes"]


EXPECTATIONS = [
    # The shape slice 1 exists for: two sites, every transition determined, no payload block
    # entered twice. Must collapse to straight-line code calling A then B in that order.
    Expectation("two_site_linear", resolved=True, switch_gone=True, calls=["A", "B"]),

    # The same payload REGION is reached in two configurations -- A() twice, then B(), then exit --
    # so it must be specialised into two copies. Collapsing a state-dependent region to one answer is
    # the historic corruption; refusing it was slice 1's correct-but-limited behaviour.
    Expectation("shared_payload", resolved=True, switch_gone=True, calls=["A", "A", "B"]),

    # The dispatch value comes out of a call, so nothing about it is knowable. Must refuse.
    Expectation("call_dependent", outcome="Undetermined", resolved=False),

    # --- StateMachineTracer: proving a machine cannot terminate ---------------------------------
    # The exit is a switch target in both, so it is CFG-reachable and the body verifies. What differs
    # is whether any value the machine can produce selects it -- which is the distinction a plain
    # reachability walk cannot make, and the reason one was tried and reverted.

    # All feeders are constants and none is the exit index. Provable: reject.
    Expectation("dead_exit_constant_states",
                log_contains=["Dispatch resolution rejected"]),

    # The feeder is a call, so every target stays possible and the exit is reachable. Must NOT be
    # rejected -- the safety direction, where imprecision costs a missed defect and never a deleted
    # correct resolution.
    Expectation("dead_exit_unknown_feeder",
                log_lacks=["Dispatch resolution rejected"]),

    # One arm returns, the other cycles forever, and the branch is untracked. The exit shows up in
    # the widened set, so the verdict must be exit-reachable -- which is precisely NOT a proof that
    # the method terminates, and is why the verdict carries that name. Nothing may be rejected here.
    Expectation("exit_reachable_not_proven",
                # The verdict itself is not observable here -- the end-of-run gate only reports
                # methods that still carry a switch. What IS observable is the claim that matters,
                # and it is the exact inverse of dead_exit_constant_states, which reports
                # "1 non-terminating" for a body of otherwise identical shape.
                log_contains=["State-machine trace: 0 non-terminating"],
                log_lacks=["Dispatch resolution rejected"]),

    # --- Costura.Fody extraction ---------------------------------------------------------------
    # Four resources, and only two are assemblies. The two that are must come out byte-identical --
    # including through raw deflate -- and the .pdb and the non-PE must be declined rather than
    # written somewhere that can only fail to load them.
    Expectation("costura_host",
                source=costura_host_il,
                blobs=costura_blobs,
                log_contains=["Costura: extracting 2 embedded"],
                log_lacks=["notes.pdb", "bogus.dll", "could not write"],
                files_written=["plain.dll", "packed.dll"]),

    # --- CflowConstantsInliner: the premise check -------------------------------------------------
    # The corpus cannot test any of this. There the .cctor always calls the initialiser, so the
    # refusal path never runs and a regression in it would be invisible until an assembly that needs
    # it shows up -- as a silently wrong branch, since these constants feed opaque predicates.

    # Justified: the type's .cctor calls the initialiser, so the CLR runs it before the first read of
    # any field on that type. Fold, and the now-unused constants type goes away.
    Expectation("cflow_cctor_calls_init",
                source=lambda: cflow_constants_il([("Consts", "    call void Consts::Init()")],
                                                  reads=["Consts::f7", "Consts::f11"]),
                log_contains=["Cflow constants:", "inlined at"],
                log_lacks=["not folding"],
                body_lacks=["ldsfld"],
                il_lacks=["Consts"]),

    # No .cctor at all: nothing makes the stores happen before the reads, so the constants must be
    # left alone -- AND the type must survive, because the ldsfld sites still refer to it.
    Expectation("cflow_no_cctor",
                source=lambda: cflow_constants_il([("Consts", None)],
                                                  reads=["Consts::f7", "Consts::f11"]),
                log_contains=["not folding", ".cctor does not call it"],
                il_contains=["Consts", "ldsfld"]),

    # The discriminating case: the initialiser IS called, but from an ordinary method rather than
    # the .cctor. A check that counted callers would accept this; nothing here says Helper() ever
    # runs, so the fields may well still be 0 when Run() reads them. Must refuse.
    # #15: the first shape match fails the premise and the second passes. Selection must move on
    # rather than treat the first as final -- ConstsB's constants are foldable and forfeiting them
    # would be a silent readability loss. ConstsA's reads must survive untouched.
    Expectation("cflow_second_candidate",
                source=lambda: cflow_constants_il(
                    [("ConstsA", None), ("ConstsB", "    call void ConstsB::Init()")],
                    reads=["ConstsA::f3", "ConstsB::f7"]),
                log_contains=["not folding", "ConstsA::Init", "ConstsB::Init", "inlined at"],
                il_contains=["ConstsA", "ConstsA::f3"],
                il_lacks=["ConstsB::f7"]),

    Expectation("cflow_called_outside_cctor",
                source=lambda: cflow_constants_il([("Consts", "    call void Consts::Unrelated()")],
                                                  reads=["Consts::f7", "Consts::f11"],
                                                  helper_for="Consts"),
                log_contains=["not folding", "Target::Helper"],
                il_contains=["Consts", "ldsfld"]),
]



def find_ilasm(fetch: bool) -> Path:
    found = shutil.which("ilasm")
    if found:
        return Path(found)
    cached = (Path.home() / ".nuget" / "packages" / ILASM_PKG.lower() / ILASM_VER
              / "runtimes" / RID / "native" / f"ilasm{EXE}")
    if cached.exists():
        return cached
    if not fetch:
        sys.exit(f"error: ilasm not found on PATH or at {cached}\n"
                 f"  Re-run with --fetch-tools to restore {ILASM_PKG} {ILASM_VER} from NuGet.")

    print(f"Restoring {ILASM_PKG} {ILASM_VER} ...")
    with tempfile.TemporaryDirectory() as tmp:
        proj = Path(tmp) / "fetch.csproj"
        proj.write_text(
            '<Project Sdk="Microsoft.NET.Sdk">'
            '<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>'
            f'<ItemGroup><PackageDownload Include="{ILASM_PKG}" Version="[{ILASM_VER}]" /></ItemGroup>'
            '</Project>')
        subprocess.run(["dotnet", "restore", str(proj)], check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    if not cached.exists():
        sys.exit(f"error: restore completed but {cached} is still missing")
    cached.chmod(0o755)
    return cached


def find_de4dot() -> list[str]:
    """The built de4dot, discovered rather than hardcoded -- the target framework moves upstream."""
    candidates = sorted((ROOT / "Release").glob(f"net*/{RID}/de4dot.dll"))
    if not candidates:
        sys.exit(f"error: no de4dot build under {ROOT / 'Release'}/net*/{RID}/\n"
                 f"  Build it first:  dotnet build -c Release de4dot.net.slnf")
    return ["dotnet", str(candidates[-1])]


OUTCOME_RE = re.compile(r"relational: outcome=(\w+)")
RESOLVED_RE = re.compile(r"XOR-switch relational: resolved (\d+) (?:edge|step)")


def run_fixture(exp: Expectation, ilasm: Path, de4dot: list[str], workdir: Path) -> list[str]:
    """Returns a list of failure descriptions; empty means the fixture passed."""
    if exp.blobs is not None:
        for blob_name, blob in exp.blobs().items():
            (workdir / blob_name).write_bytes(blob)

    if exp.source is not None:
        # Generated: write it into the workspace so --keep leaves the exact IL that was assembled,
        # not a template that has to be re-rendered to be read.
        src = workdir / f"{exp.name}.il"
        src.write_text(exp.source())
    else:
        src = FIXTURES / f"{exp.name}.il"
        if not src.exists():
            return [f"fixture source missing: {src}"]

    dll = workdir / f"{exp.name}.dll"
    out = workdir / f"{exp.name}-out.dll"
    # Option prefix differs by platform: the Windows build takes /DLL, the Unix build -dll.
    flag = "/" if sys.platform == "win32" else "-"
    asm = subprocess.run([str(ilasm), f"{flag}dll", f"{flag}quiet", str(src),
                          f"{flag}output={dll}"], capture_output=True, text=True, cwd=workdir)
    if asm.returncode != 0 or not dll.exists():
        return [f"ilasm failed:\n{asm.stdout[-1500:]}{asm.stderr[-1500:]}"]

    env = dict(os.environ, DE4DOT_XORSWITCH_TRACE="Run")
    # `-p dr4` forces the .NET Reactor v4 deobfuscator: these fixtures carry no Reactor markers, so
    # detection would score them as something else and the pass under test would never run. It is a
    # PER-FILE option, so it follows the filename -- de4dot silently mis-parses it before. `-v`
    # is global and must precede the file; the resolver logs its decision at verbose level.
    proc = subprocess.run(de4dot + ["-v", str(dll), "-p", "dr4", "-o", str(out)],
                          capture_output=True, text=True, env=env)
    log = proc.stdout + proc.stderr
    (workdir / f"{exp.name}.log").write_text(log)
    if proc.returncode != 0 or not out.exists():
        return [f"de4dot failed (exit {proc.returncode}):\n{log[-1500:]}"]

    failures = []
    outcomes = OUTCOME_RE.findall(log)
    applied = RESOLVED_RE.search(log) is not None

    if exp.resolved is True and not applied:
        failures.append(f"expected the resolver to apply a plan; outcomes seen: {outcomes or 'none'}")
    if exp.resolved is False and applied:
        failures.append("resolver applied a plan, but this fixture must be refused")
    if exp.outcome and exp.outcome not in outcomes:
        failures.append(f"expected outcome {exp.outcome}, got {outcomes or 'none'}")

    for name in exp.files_written:
        if not (workdir / name).exists():
            failures.append(f"expected de4dot to write {name} beside its output")

    for text in exp.log_contains:
        if text not in log:
            failures.append(f"expected {text!r} in de4dot's output")
    for text in exp.log_lacks:
        if text in log:
            failures.append(f"did not expect {text!r} in de4dot's output")

    if exp.il_contains or exp.il_lacks:
        full = disassemble(out, workdir, exp.name)
        if full is None:
            failures.append("could not disassemble the output module")
        else:
            for text in exp.il_contains:
                if text not in full:
                    failures.append(f"expected {text!r} to survive in the output module")
            for text in exp.il_lacks:
                if text in full:
                    failures.append(f"expected {text!r} to be gone from the output module")

    if exp.switch_gone is not None or exp.calls is not None or exp.body_lacks:
        body = disassemble_run(out, workdir, exp.name)
        if body is None:
            failures.append("could not read Target::Run back out of the output")
        else:
            if exp.switch_gone and "switch" in body:
                failures.append("a switch survived in Target::Run; the machine was not collapsed")
            if exp.calls is not None:
                got = re.findall(r"Marker::(\w+)\(\)", body)
                if got != exp.calls:
                    failures.append(f"payload sequence is {got}, expected {exp.calls}")
            for text in exp.body_lacks:
                if text in body:
                    failures.append(f"expected {text!r} to be gone from Target::Run")
    return failures


def disassemble(dll: Path, workdir: Path, name: str) -> str | None:
    """The whole output module, via ildasm if present -- otherwise IL checks are skipped, not faked."""
    ildasm = shutil.which("ildasm") or str(
        Path.home() / ".nuget" / "packages" / f"runtime.{RID}.microsoft.netcore.ildasm"
        / ILASM_VER / "runtimes" / RID / "native" / f"ildasm{EXE}")
    if not Path(ildasm).exists():
        return None
    dump = workdir / f"{name}-out.il"
    flag = "/" if sys.platform == "win32" else "-"
    proc = subprocess.run([ildasm, str(dll), f"{flag}out={dump}"], capture_output=True, text=True)
    if proc.returncode != 0 or not dump.exists():
        return None
    return dump.read_text(errors="ignore")


def disassemble_run(dll: Path, workdir: Path, name: str) -> str | None:
    """Target::Run's body alone."""
    text = disassemble(dll, workdir, name)
    if text is None:
        return None
    match = re.search(r"\.method[^{]*?Run\(\)[^{]*?\{(.*?)\n  \}", text, re.S)
    return match.group(1) if match else None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--fetch-tools", action="store_true",
                        help="restore ilasm from NuGet if it is not already available")
    parser.add_argument("--keep", action="store_true", help="keep the working directory")
    args = parser.parse_args()

    ilasm = find_ilasm(args.fetch_tools)
    de4dot = find_de4dot()
    workdir = Path(tempfile.mkdtemp(prefix="reactor-tests-"))

    failed = 0
    try:
        for exp in EXPECTATIONS:
            problems = run_fixture(exp, ilasm, de4dot, workdir)
            if problems:
                failed += 1
                print(f"FAIL  {exp.name}")
                for line in problems:
                    print(f"        {line}")
            else:
                print(f"ok    {exp.name}")
    finally:
        if args.keep:
            print(f"\nworking directory kept: {workdir}")
        else:
            shutil.rmtree(workdir, ignore_errors=True)

    print(f"\n{len(EXPECTATIONS) - failed}/{len(EXPECTATIONS)} passed")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
