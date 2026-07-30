/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.Collections.Generic;
using System.Linq;
using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4.xorswitch;

struct DispatchNode {
	public Block SwitchBlock { get; init; }
	public Block HeaderBlock { get; init; }
	public Local StateVar { get; init; }
	public IList<Block> CaseTargets { get; init; }
	public Dictionary<Block, int> BlockToCase { get; init; }
}

/// <summary>
///     Detects whether a switch block is a flattened dispatch using abstract interpretation.
/// </summary>
static class DispatchDetector {
	const int MaxBfsBlocks = 5000;

	public static DispatchNode? TryDetect(Block switchBlock, Blocks blocks) {
		if (switchBlock.Targets is null || switchBlock.Targets.Count < 2)
			return null;

		// Detect split dispatch: if switchBlock has only the switch instruction,
		// look for a single predecessor that falls through containing the dispatch computation
		Block headerBlock = null;
		if (NeedsHeaderBlock(switchBlock)) {
			headerBlock = FindHeaderBlock(switchBlock);
		}

		var stateVar = FindStateVar(switchBlock, headerBlock, blocks);
		// stateVar may be null for stack-based state

		// Validate: emulate with a probe value and check that we get a definite case index
		if (!ValidateDispatch(switchBlock, headerBlock, stateVar, blocks))
			return null;

		var blockToCase = BuildBlockToCase(switchBlock, headerBlock, XorSwitchTrace.Wants(blocks.Method));

		// Heuristic gate: require that at least some predecessors are case targets,
		// indicating a state machine (cycle exists) rather than a legitimate switch
		if (!HasCyclicPredecessors(switchBlock, headerBlock, blockToCase))
			return null;

		return new DispatchNode {
			SwitchBlock = switchBlock,
			HeaderBlock = headerBlock,
			StateVar = stateVar,
			CaseTargets = switchBlock.Targets,
			BlockToCase = blockToCase,
		};
	}

	/// <summary>
	///     Returns true if the switch block contains only the switch instruction
	///     (or only nops + switch) and needs a separate header block for the dispatch computation.
	/// </summary>
	static bool NeedsHeaderBlock(Block switchBlock) {
		var instrs = switchBlock.Instructions;
		// Check if there's no stloc and the block is very short (just the switch)
		for (int i = 0; i < instrs.Count - 1; i++) {
			if (instrs[i].IsStloc())
				return false;
			if (instrs[i].IsLdcI4())
				return false; // has constants — full dispatch block
		}
		return true;
	}

	/// <summary>
	///     Finds the single predecessor block that falls through to the switch block
	///     and contains the XOR dispatch computation (xor + rem.un). This handles
	///     the case where the blocks framework splits the dispatch header from the
	///     switch instruction because the switch is a branch target.
	/// </summary>
	static Block FindHeaderBlock(Block switchBlock) {
		foreach (var src in switchBlock.Sources) {
			if (src == switchBlock)
				continue;
			if (src.LastInstr.OpCode.Code == Code.Switch)
				continue;
			// Must fall through to the switch block
			if (src.FallThrough != switchBlock)
				continue;
			// Must contain the XOR dispatch computation (xor + rem.un)
			if (!HasXorDispatchPattern(src))
				continue;
			return src;
		}
		return null;
	}

	/// <summary>
	///     Checks if a block contains both xor and rem.un instructions,
	///     indicating it holds the XOR dispatch computation.
	/// </summary>
	static bool HasXorDispatchPattern(Block block) {
		var instrs = block.Instructions;
		bool hasXor = false, hasRemUn = false;
		foreach (var t in instrs) {
			var code = t.OpCode.Code;
			if (code == Code.Xor) hasXor = true;
			else if (code == Code.Rem_Un) hasRemUn = true;
		}
		return hasXor && hasRemUn;
	}

	/// <summary>
	///     Finds the state variable by looking for stloc in the switch block and/or header
	///     block (the local that stores the XOR result). Also handles the case where a local
	///     is loaded as input.
	/// </summary>
	static Local FindStateVar(Block switchBlock, Block headerBlock, Blocks blocks) {
		// Strategy 1: Look for stloc in the switch block — in the pattern
		// ldc.i4 K; xor; dup; stloc.N; ldc.i4 M; rem.un; switch
		// the stloc.N local is the state variable
		foreach (var instr in switchBlock.Instructions) {
			if (instr.IsStloc()) {
				var local = instr.Instruction.GetLocal(blocks.Locals);
				if (local is not null)
					return local;
			}
		}

		// Strategy 1b: Look for stloc in the header block
		if (headerBlock is not null) {
			foreach (var instr in headerBlock.Instructions) {
				if (instr.IsStloc()) {
					var local = instr.Instruction.GetLocal(blocks.Locals);
					if (local is not null)
						return local;
				}
			}
		}

		// Strategy 2: For each local L, emulate the dispatch blocks with L set to a known
		// probe value. If the top of stack is AllBitsValid, L is the state var.
		const int probeValue = 0x12345678;
		var method = blocks.Method;
		foreach (var local in blocks.Locals) {
			var emu = new InstructionEmulator();
			emu.Initialize(method, false);
			emu.SetLocal(local, new Int32Value(probeValue));

			try {
				if (headerBlock is not null) {
					var hdrInstrs = headerBlock.Instructions;
					int hdrEnd = hdrInstrs.Count;
					if (hdrEnd > 0 && hdrInstrs[hdrEnd - 1].IsBr())
						hdrEnd--;
					emu.Emulate(hdrInstrs, 0, hdrEnd);
				}

				var instrs = switchBlock.Instructions;
				emu.Emulate(instrs, 0, instrs.Count - 1);
			}
			catch {
				continue;
			}

			if (emu.StackSize() < 1)
				continue;

			var tos = emu.Pop();
			if (tos is Int32Value iv && iv.AllBitsValid())
				return local;
		}

		return null;
	}

	/// <summary>
	///     Determines the number of stack values the dispatch blocks expect at entry.
	/// </summary>
	static int ComputeStackInput(Block switchBlock, Block headerBlock) {
		int depth = 0;
		int minDepth = 0;

		if (headerBlock is not null) {
			var hdrInstrs = headerBlock.Instructions;
			int hdrEnd = hdrInstrs.Count;
			if (hdrEnd > 0 && hdrInstrs[hdrEnd - 1].IsBr())
				hdrEnd--;
			TrackDepth(hdrInstrs, hdrEnd, ref depth, ref minDepth);
		}

		var instrs = switchBlock.Instructions;
		TrackDepth(instrs, instrs.Count - 1, ref depth, ref minDepth);

		return -minDepth;
	}

	static void TrackDepth(IList<Instr> instrs, int end, ref int depth, ref int minDepth) {
		for (int i = 0; i < end; i++) {
			instrs[i].Instruction.CalculateStackUsage(false, out int pushes, out int pops);
			if (pops == -1) { depth = 0; continue; }
			depth -= pops;
			if (depth < minDepth)
				minDepth = depth;
			depth += pushes;
		}
	}

	/// <summary>
	///     Emulate the dispatch blocks with probe values and verify that TOS
	///     produces a known (AllBitsValid) switch index.
	/// </summary>
	static bool ValidateDispatch(Block switchBlock, Block headerBlock, Local stateVar, Blocks blocks) {
		var method = blocks.Method;
		var emu = new InstructionEmulator();
		emu.Initialize(method, false);

		if (stateVar is not null)
			emu.SetLocal(stateVar, new Int32Value(0));

		// Push probe values for any stack inputs the dispatch blocks expect
		int stackInputs = ComputeStackInput(switchBlock, headerBlock);
		for (int i = 0; i < stackInputs; i++)
			emu.Push(new Int32Value(0));

		try {
			if (headerBlock is not null) {
				var hdrInstrs = headerBlock.Instructions;
				int hdrEnd = hdrInstrs.Count;
				if (hdrEnd > 0 && hdrInstrs[hdrEnd - 1].IsBr())
					hdrEnd--;
				emu.Emulate(hdrInstrs, 0, hdrEnd);
			}

			var instrs = switchBlock.Instructions;
			emu.Emulate(instrs, 0, instrs.Count - 1);
		}
		catch {
			return false;
		}

		if (emu.StackSize() < 1)
			return false;

		var tos = emu.Pop();
		return tos is Int32Value iv && iv.AllBitsValid();
	}

	/// <summary>
	///     BFS from each case target, mapping reachable blocks to their case index.
	///     Blocks reachable from multiple case indices are excluded (ambiguous).
	/// </summary>
	static Dictionary<Block, int> BuildBlockToCase(Block switchBlock, Block headerBlock, bool trace) {
		var result = new Dictionary<Block, int>();
		var ambiguous = new HashSet<Block>();
		var targets = switchBlock.Targets;

		for (int caseIdx = 0; caseIdx < targets.Count; caseIdx++) {
			var queue = new Queue<Block>();
			var visited = new HashSet<Block>();
			var parent = new Dictionary<Block, Block>();
			queue.Enqueue(targets[caseIdx]);
			visited.Add(targets[caseIdx]);
			int count = 0;

			while (queue.Count > 0 && count < MaxBfsBlocks) {
				var block = queue.Dequeue();
				count++;

				if (block == switchBlock || block == headerBlock)
					continue;

				if (ambiguous.Contains(block))
					continue;

				if (result.TryGetValue(block, out int existingCase)) {
					if (existingCase != caseIdx) {
						if (trace)
							XorSwitchTrace.Log($"  attribution: {XorSwitchTrace.Id(block)} AMBIGUOUS (claimed by case {existingCase}, also reached from case {caseIdx}); this search stops here");
						result.Remove(block);
						ambiguous.Add(block);
					}
					continue;
				}

				result[block] = caseIdx;
				if (trace)
					XorSwitchTrace.Log($"  attribution: {XorSwitchTrace.Id(block)} <- case {caseIdx} via {DescribeRoute(block, parent, targets[caseIdx])}");

				foreach (var succ in block.GetTargets()) {
					if (visited.Add(succ)) {
						parent[succ] = block;
						queue.Enqueue(succ);
					}
				}
			}
		}

		return result;
	}

	/// <summary>Renders the BFS route that attributed a block to a case, oldest-first.</summary>
	static string DescribeRoute(Block block, Dictionary<Block, Block> parent, Block root) {
		var steps = new List<string>();
		var current = block;
		while (current is not null && steps.Count < 12) {
			steps.Add($"{XorSwitchTrace.Id(current)}({current.LastInstr.OpCode.Name})");
			if (current == root)
				break;
			parent.TryGetValue(current, out current);
		}
		steps.Reverse();
		return string.Join(" -> ", steps);
	}

	/// <summary>
	///     Check that at least one predecessor of the dispatch (switch block or header block)
	///     is also a case target or owned by a case, indicating a state machine cycle.
	/// </summary>
	static bool HasCyclicPredecessors(Block switchBlock, Block headerBlock, Dictionary<Block, int> blockToCase) {
		foreach (var source in switchBlock.Sources) {
			if (blockToCase.ContainsKey(source))
				return true;
		}
		if (headerBlock is not null) {
			if (headerBlock.Sources.Where(source => source != switchBlock).Any(blockToCase.ContainsKey))
			{
				return true;
			}
		}
		// Also check if any case target directly feeds back to the dispatch
		if (switchBlock.Targets is not null) {
			return switchBlock.Targets.Any(target => target.GetTargets().Any(succ => succ == switchBlock || succ == headerBlock));
		}
		return false;
	}
}
