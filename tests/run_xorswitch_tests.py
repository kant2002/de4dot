#!/usr/bin/env python3
"""
Run the XorSwitch IL fixtures: assemble -> de4dot -> check what the resolver decided.

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

    python3 tests/run_xorswitch_tests.py [--fetch-tools] [--keep]
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
    """What a fixture must make the relational resolver decide."""

    def __init__(self, name, outcome=None, resolved=None, switch_gone=None, calls=None):
        self.name = name
        self.outcome = outcome            # substring expected in the resolver's outcome line
        self.resolved = resolved          # True: it applied a plan; False: it must not have
        self.switch_gone = switch_gone    # True: no switch left in Target::Run
        self.calls = calls                # exact order of Marker calls left in Target::Run


EXPECTATIONS = [
    # The shape slice 1 exists for: two sites, every transition determined, no payload block
    # entered twice. Must collapse to straight-line code calling A then B in that order.
    Expectation("two_site_linear", resolved=True, switch_gone=True, calls=["A", "B"]),

    # The same payload block is reached in two different configurations, so resolving it needs
    # specialising the block into two copies. Slice 1 must REFUSE, not pick one of the two answers --
    # collapsing a state-dependent block to a single target is the historic corruption.
    Expectation("shared_payload", outcome="RevisitedBlock", resolved=False),

    # The dispatch value comes out of a call, so nothing about it is knowable. Must refuse.
    Expectation("call_dependent", outcome="Undetermined", resolved=False),
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
RESOLVED_RE = re.compile(r"XOR-switch relational: resolved (\d+) edge")


def run_fixture(exp: Expectation, ilasm: Path, de4dot: list[str], workdir: Path) -> list[str]:
    """Returns a list of failure descriptions; empty means the fixture passed."""
    src = FIXTURES / f"{exp.name}.il"
    if not src.exists():
        return [f"fixture source missing: {src}"]

    dll = workdir / f"{exp.name}.dll"
    out = workdir / f"{exp.name}-out.dll"
    # Option prefix differs by platform: the Windows build takes /DLL, the Unix build -dll.
    flag = "/" if sys.platform == "win32" else "-"
    asm = subprocess.run([str(ilasm), f"{flag}dll", f"{flag}quiet", str(src),
                          f"{flag}output={dll}"], capture_output=True, text=True)
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

    if exp.switch_gone is not None or exp.calls is not None:
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
    return failures


def disassemble_run(dll: Path, workdir: Path, name: str) -> str | None:
    """Target::Run's body, via ildasm if present -- otherwise the IL checks are skipped, not faked."""
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
    text = dump.read_text(errors="ignore")
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
    workdir = Path(tempfile.mkdtemp(prefix="xorswitch-tests-"))

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
