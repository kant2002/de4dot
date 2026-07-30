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

using System;
using System.Collections.Generic;
using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4.xorswitch;

/// <summary>
///     Resolves a method whose control flow is driven by <b>two or more</b> switch dispatches feeding
///     each other — the chained shape <c>EdgeResolver</c> structurally cannot do, because the state a
///     dispatch predecessor is entered with depends on which arm of the *other* dispatch fired.
///
///     <para>
///     The representation change is that there is no seed and no case attribution. The machine is
///     interpreted forward from the method's first instruction with a real abstract emulator, so the
///     configuration — pending evaluation-stack values in order, every local, and which dispatch is
///     next — is carried along the edge instead of being reconstructed from block ownership. That is
///     what removes the whole family of defects recorded in ROADMAP §5: no <c>BlockToCase</c>, no
///     seed guessing, no <c>VerifySeedRoutesToCase</c>.
///     </para>
///
///     <para>
///     <b>Slice 1 deliberately handles only the case that needs no block duplication.</b> Every
///     transition must be determined, and no block may be entered twice — so the explored machine is
///     a single finite path ending at a real exit, and applying it is edge relinking plus removing
///     the state pushes. A block reachable in two different configurations needs specialising into
///     two copies; that is slice 2 and this refuses it rather than collapsing it to one answer, which
///     is precisely the "partial resolution is reliably destructive" failure.
///     </para>
///
///     <para>
///     Nothing is mutated until the whole walk has succeeded. A plan that runs out of budget, meets
///     an undetermined branch, revisits a block, or cannot cut a predecessor's state push cleanly is
///     discarded whole — the method is then left exactly as it was for the existing single-site path
///     to handle.
///     </para>
/// </summary>
static class RelationalDispatchResolver {
	// Independent budgets: a configuration sequence can be non-repeating and still grow impractically,
	// so "no repeat yet" is not on its own a reason to keep going.
	const int MaxSteps = 512;
	const int MaxVisitedBlocks = 256;
	const int MinDispatchSites = 2;

	/// <summary>One predecessor edge to rewrite: cut the state push, branch straight to the target.</summary>
	readonly struct PlannedEdge {
		public PlannedEdge(Block predecessor, Block target, int instrsToRemove) {
			Predecessor = predecessor;
			Target = target;
			InstrsToRemove = instrsToRemove;
		}

		public Block Predecessor { get; }
		public Block Target { get; }
		public int InstrsToRemove { get; }
	}

	/// <summary>Why a walk stopped. Only <see cref="Exit"/> is a result worth applying.</summary>
	enum Outcome {
		Exit,               // ret / throw / rethrow reached -- the machine provably terminates
		Undetermined,       // a branch or dispatch index that is not a known constant
		RevisitedBlock,     // needs specialisation -- slice 2
		BudgetExhausted,
		UncuttablePush,     // the state push is not a removable pure suffix
		LeftTheMachine,     // control reached something this model does not describe
	}

	public static bool TryResolve(Blocks blocks, List<Block> allBlocks) {
		if (allBlocks.Count == 0)
			return false;

		var sites = new HashSet<Block>();
		foreach (var block in allBlocks) {
			if (block.LastInstr.OpCode.Code == Code.Switch && block.Targets is { Count: >= 2 })
				sites.Add(block);
		}
		bool trace = XorSwitchTrace.Wants(blocks.Method);
		if (trace)
			XorSwitchTrace.Log($"relational: {sites.Count} dispatch site(s), {allBlocks.Count} blocks");
		if (sites.Count < MinDispatchSites)
			return false;

		var plan = new List<PlannedEdge>();
		var outcome = Walk(blocks, allBlocks[0], sites, plan, out int sitesUsed, trace);
		if (trace)
			XorSwitchTrace.Log($"relational: outcome={outcome} sitesUsed={sitesUsed} edges={plan.Count}");
		if (outcome != Outcome.Exit || sitesUsed < MinDispatchSites || plan.Count == 0)
			return false;

		// One predecessor may only be rewritten once; two plans for the same block would mean the
		// walk passed through it twice, which RevisitedBlock should already have caught.
		var predecessors = new HashSet<Block>();
		foreach (var edge in plan) {
			if (!predecessors.Add(edge.Predecessor))
				return false;
		}

		foreach (var edge in plan)
			edge.Predecessor.ReplaceLastInstrsWithBranch(edge.InstrsToRemove, edge.Target);

		Logger.v("  XOR-switch relational: resolved {0} edge(s) across {1} dispatch site(s) in {2}",
			plan.Count, sitesUsed, blocks.Method?.Name ?? "?");
		return true;
	}

	/// <summary>
	///     Interpret the method forward from its first instruction, recording the edge each dispatch
	///     predecessor should become. Mutates nothing.
	/// </summary>
	static Outcome Walk(Blocks blocks, Block entry, HashSet<Block> sites, List<PlannedEdge> plan,
			out int sitesUsed, bool trace = false) {
		sitesUsed = 0;
		var usedSites = new HashSet<Block>();
		var visited = new HashSet<Block>();

		var emu = new InstructionEmulator();
		// From the first instruction, so `.locals init` zeroing is real rather than assumed -- the
		// one place that claim is sound. See ROADMAP §7 item 4 for what assuming it elsewhere cost.
		emu.Initialize(blocks, true);

		var current = entry;
		Block? pendingPredecessor = null;
		int pendingPredecessorDepth = 0;

		for (int step = 0; step < MaxSteps; step++) {
			if (current is null)
				return Outcome.LeftTheMachine;

			int entryDepth = emu.StackSize();
			bool isSite = sites.Contains(current);
			// Only payload blocks are held to "entered at most once". A dispatch site is re-entered
			// on every iteration of the machine by construction -- that is the loop going around --
			// and it carries no state of its own: it consumes the pending value and branches. What
			// needs specialising, and so is refused here, is a payload block reached in two different
			// configurations. MaxSteps still bounds a machine that only ever cycles between sites.
			if (!isSite) {
				if (!visited.Add(current))
					return Outcome.RevisitedBlock;
				if (visited.Count > MaxVisitedBlocks)
					return Outcome.BudgetExhausted;
			}
			if (trace)
				XorSwitchTrace.Log($"  step {step}: {XorSwitchTrace.Id(current)} site={isSite} [{XorSwitchTrace.Sketch(current)}]");
			var instrs = current.Instructions;
			int end = instrs.Count;
			// The terminator is interpreted below, not emulated, so the operands it consumes are
			// still on the abstract stack when we get there.
			if (end > 0 && (instrs[end - 1].IsBr() || instrs[end - 1].IsConditionalBranch()
					|| instrs[end - 1].OpCode.Code == Code.Switch))
				end--;

			try {
				emu.Emulate(instrs, 0, end);
			}
			catch {
				return Outcome.Undetermined;
			}

			if (isSite) {
				if (pendingPredecessor is null)
					return Outcome.LeftTheMachine;   // a dispatch as the very first block: not this shape

				var tos = emu.Pop();
				if (tos is not Int32Value index || !index.AllBitsValid())
					return Outcome.Undetermined;

				// `switch` semantics: an index outside the table falls through to the next
				// instruction rather than being invalid.
				var targets = current.Targets;
				var target = index.Value >= 0 && targets is not null && index.Value < targets.Count
					? targets[index.Value]
					: current.FallThrough;
				if (target is null)
					return Outcome.Undetermined;

				int cut = FindPureStatePushSuffix(pendingPredecessor, pendingPredecessorDepth);
				if (cut < 0)
					return Outcome.UncuttablePush;

				plan.Add(new PlannedEdge(pendingPredecessor, target, cut));
				usedSites.Add(current);
				sitesUsed = usedSites.Count;
				pendingPredecessor = null;
				current = target;
				continue;
			}

			var last = current.LastInstr;
			switch (last.OpCode.Code) {
			case Code.Ret:
			case Code.Throw:
			case Code.Rethrow:
				return Outcome.Exit;
			}

			Block? next;
			if (last.IsConditionalBranch()) {
				// Reactor gates the arms of these machines with opaque predicates -- `ldc.i4 0;
				// ldc.i4 <folded constant>; brfalse`. By the time this pass sees the method the
				// constant is already inlined, so the branch is determined and following it is not a
				// fork. Anything the emulator cannot decide still stops the walk.
				next = ResolveConditional(emu, current, last);
				if (next is null)
					return Outcome.Undetermined;
			}
			else {
				next = current.GetOnlyTarget();
				if (next is null)
					return Outcome.LeftTheMachine;
			}

			pendingPredecessor = current;
			pendingPredecessorDepth = entryDepth;
			current = next;
		}

		return Outcome.BudgetExhausted;
	}

	/// <summary>
	///     The successor of a conditional branch whose condition the emulator knows, or null.
	///     Only the two forms Reactor's opaque predicates use are decided; everything else stops the
	///     walk rather than being guessed.
	/// </summary>
	static Block? ResolveConditional(InstructionEmulator emu, Block block, Instr last) {
		bool wantTrue;
		switch (last.OpCode.Code) {
		case Code.Brtrue: case Code.Brtrue_S: wantTrue = true; break;
		case Code.Brfalse: case Code.Brfalse_S: wantTrue = false; break;
		default: return null;
		}

		if (emu.StackSize() < 1)
			return null;
		var cond = emu.Pop();
		if (cond is not Int32Value value || !value.AllBitsValid())
			return null;

		bool taken = (value.Value != 0) == wantTrue;
		if (!taken)
			return block.FallThrough;
		return block.Targets is { Count: 1 } ? block.Targets[0] : null;
	}

	/// <summary>
	///     How many trailing instructions of <paramref name="block"/> make up the state push the next
	///     dispatch consumes, or -1 if they cannot be removed cleanly.
	///
	///     <para>
	///     The count includes the block's trailing <c>br</c>, so the result is directly usable as
	///     <c>ReplaceLastInstrsWithBranch</c>'s argument. Every instruction removed must be
	///     side-effect-free: cutting the branch alone and leaving the push is what left ~184
	///     stack-depth errors behind in the third of the earlier attempts (ROADMAP §7 item 3).
	///     </para>
	/// </summary>
	static int FindPureStatePushSuffix(Block block, int entryDepth) {
		var instrs = block.Instructions;
		int count = instrs.Count;
		// The terminator goes too, whether it is a `br` or an opaque predicate the walk already
		// determined. Removing the branch but leaving its condition operands or the state push is
		// what unbalanced the stack in the third earlier attempt.
		int branch = count > 0 && (instrs[count - 1].IsBr() || instrs[count - 1].IsConditionalBranch())
			? 1 : 0;
		int last = count - branch;             // exclusive end of the value-producing part

		// Depths must start from what the walk actually observed at this block's entry, not from an
		// assumed zero. These machines carry state on the stack, so a block that begins by consuming
		// an incoming value is normal -- measuring it from zero reads as an underflow and rejects the
		// commonest shape there is.
		var depths = new int[last + 1];
		depths[0] = entryDepth;
		for (int i = 0; i < last; i++) {
			instrs[i].Instruction.CalculateStackUsage(false, out int pushes, out int pops);
			if (pops == -1)
				return -1;                      // clears the stack; not something to cut across
			int depth = depths[i] - pops;
			if (depth < 0)
				return -1;
			depths[i + 1] = depth + pushes;
		}

		// After the cut the block must leave the stack at the machine's baseline, because it will
		// branch straight to a payload block instead of to the dispatch that would have consumed the
		// pushed state. Anything else is the unbalanced-stack failure by another route.
		for (int i = last - 1; i >= 0; i--) {
			if (!IsSideEffectFree(instrs[i]))
				return -1;
			if (depths[i] == 0)
				return (last - i) + branch;
		}
		return -1;
	}

	static bool IsSideEffectFree(Instr instr) {
		switch (instr.OpCode.Code) {
		case Code.Ldc_I4: case Code.Ldc_I4_S:
		case Code.Ldc_I4_0: case Code.Ldc_I4_1: case Code.Ldc_I4_2: case Code.Ldc_I4_3:
		case Code.Ldc_I4_4: case Code.Ldc_I4_5: case Code.Ldc_I4_6: case Code.Ldc_I4_7:
		case Code.Ldc_I4_8: case Code.Ldc_I4_M1:
		case Code.Ldloc: case Code.Ldloc_S:
		case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
		case Code.Ldarg: case Code.Ldarg_S:
		case Code.Ldarg_0: case Code.Ldarg_1: case Code.Ldarg_2: case Code.Ldarg_3:
		case Code.Dup: case Code.Nop:
		case Code.Add: case Code.Sub: case Code.Mul:
		case Code.And: case Code.Or: case Code.Xor: case Code.Not: case Code.Neg:
		case Code.Shl: case Code.Shr: case Code.Shr_Un:
		case Code.Conv_I4: case Code.Conv_U4: case Code.Conv_I: case Code.Conv_U:
			return true;
		default:
			return false;
		}
	}
}
