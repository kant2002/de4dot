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
using de4dot.blocks;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4.xorswitch;

struct StateUpdateBoundary {
	public int CutPoint;
	public int StackDepthAtCut;
	public bool Found;
}

/// <summary>
///     Determines where payload ends and state-update begins within a predecessor block.
/// </summary>
static class StateUpdateFinder {
	public static StateUpdateBoundary Find(Block block, DispatchNode dispatch, IList<Local> allLocals) {
		if (dispatch.StateVar is not null) {
			var result = FindForLocalVar(block, dispatch.StateVar, allLocals);
			if (result.Found)
				return result;
			// Predecessor may push state on the stack instead of storing to stateVar
			// (the stloc happens in the dispatch block, not the predecessor).
			// Fall through to stack-based detection.
		}
		return FindForStack(block);
	}

	/// <summary>
	///     For local-var based state: scan backward for the last stloc to the state var,
	///     then backward-slice to find the cut point.
	/// </summary>
	static StateUpdateBoundary FindForLocalVar(Block block, Local stateVar, IList<Local> allLocals) {
		var instrs = block.Instructions;

		// Find the last store to the state variable
		int storeIdx = -1;
		for (int i = instrs.Count - 1; i >= 0; i--) {
			if (!instrs[i].IsStloc())
				continue;
			if (Instr.GetLocalVar(allLocals, instrs[i]) == stateVar) {
				storeIdx = i;
				break;
			}
		}

		if (storeIdx < 0)
			return new StateUpdateBoundary { Found = false };

		// Backward-slice from stloc: walk backward collecting instructions that feed
		// the stored value using stack depth tracking
		int cutPoint = storeIdx;
		int neededPops = 1; // stloc consumes one stack slot

		for (int i = storeIdx - 1; i >= 0 && neededPops > 0; i--) {
			instrs[i].Instruction.CalculateStackUsage(false, out int pushes, out int pops);
			if (pops == -1)
				break; // stack-clearing instruction (throw/rethrow) — stop slice
			neededPops -= pushes;
			neededPops += pops;
			cutPoint = i;
		}

		// Compute stack depth at the cut point (tolerant of incoming stack values)
		var depths = ComputeStackDepthsTolerant(instrs);
		int depthAtCut = depths[cutPoint];

		return new StateUpdateBoundary {
			CutPoint = cutPoint,
			StackDepthAtCut = depthAtCut,
			Found = true,
		};
	}

	/// <summary>
	///     For stack-based state: the state value is whatever the block pushes
	///     on the stack for the switch block to consume. Find where the extra slot begins.
	///     Tolerates blocks with incoming stack values (underflow when analyzed from depth 0).
	/// </summary>
	static StateUpdateBoundary FindForStack(Block block) {
		var instrs = block.Instructions;
		var depths = ComputeStackDepthsTolerant(instrs);

		// The last instruction may be br/fallthrough to the switch.
		// We need to find the point where the stack starts building the switch value.
		// Walk backward from end: find where stack depth increases above the entry depth.
		int endDepth = depths[depths.Length - 1];
		if (endDepth < 1)
			return new StateUpdateBoundary { Found = false };

		int targetDepth = endDepth - 1;
		int cutPoint = instrs.Count;

		for (int i = instrs.Count - 1; i >= 0; i--) {
			if (depths[i] <= targetDepth) {
				// Check if this instruction itself pushes the depth above targetDepth.
				// If so, it's the first instruction of the state update (e.g., ldc.i4 STATE)
				// and should be included in the removal range.
				instrs[i].Instruction.CalculateStackUsage(false, out int pushes, out int pops);
				int postDepth = depths[i] + pushes - (pops == -1 ? depths[i] : pops);
				if (postDepth > targetDepth)
					cutPoint = i;
				else
					cutPoint = i + 1;
				break;
			}
			if (i == 0)
				cutPoint = 0;
		}

		return new StateUpdateBoundary {
			CutPoint = cutPoint,
			StackDepthAtCut = depths[cutPoint],
			Found = true,
		};
	}

	/// <summary>
	///     Like ComputeStackDepths but tolerates blocks that receive values on the
	///     incoming stack. Tracks the minimum depth reached; if negative, shifts all
	///     depths up by |min| to account for the incoming stack values.
	/// </summary>
	static int[] ComputeStackDepthsTolerant(IList<Instr> instructions) {
		var depths = new int[instructions.Count + 1];
		int depth = 0;
		int minDepth = 0;

		for (int i = 0; i < instructions.Count; i++) {
			depths[i] = depth;
			instructions[i].Instruction.CalculateStackUsage(false, out int pushes, out int pops);
			if (pops == -1) {
				depth = 0;
			}
			else {
				depth -= pops;
				if (depth < minDepth)
					minDepth = depth;
				depth += pushes;
			}
		}

		depths[instructions.Count] = depth;

		// Shift all depths to account for incoming stack values
		if (minDepth < 0) {
			int shift = -minDepth;
			for (int i = 0; i <= instructions.Count; i++)
				depths[i] += shift;
		}

		return depths;
	}

	/// <summary>
	///     Compute stack depths before each instruction and after the last.
	///     Returns null on underflow.
	/// </summary>
	public static int[] ComputeStackDepths(IList<Instr> instructions) {
		var depths = new int[instructions.Count + 1];
		int depth = 0;

		for (int i = 0; i < instructions.Count; i++) {
			depths[i] = depth;
			instructions[i].Instruction.CalculateStackUsage(false, out int pushes, out int pops);
			if (pops == -1) {
				// Stack-clearing instruction (e.g., throw, rethrow)
				depth = 0;
			}
			else {
				depth -= pops;
				if (depth < 0)
					return null;
				depth += pushes;
			}
		}

		depths[instructions.Count] = depth;
		return depths;
	}
}
