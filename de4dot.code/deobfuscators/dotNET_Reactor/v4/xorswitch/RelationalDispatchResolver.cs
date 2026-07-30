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
using System.Linq;
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
	// Caps, fixed before the transformation was written and independent of one another: a
	// configuration sequence can be non-repeating and still grow impractically.
	const int MaxSteps = 512;               // walk steps
	const int MaxVisitedBlocks = 256;       // distinct blocks the walk may enter
	const int MaxRegionBlocks = 32;         // blocks in one single-entry region
	const int MaxRegionCopies = 8;          // copies of any one block
	const int MaxSpecialisedBlocks = 64;    // total blocks emitted by specialisation
	const int MaxAddedInstructions = 4096;  // total instructions emitted by specialisation
	const int MinDispatchSites = 2;

	/// <summary>
	///     One step: leaving <c>From</c> on its <c>FromVisit</c>-th entry, arriving at <c>To</c> on
	///     its <c>ToVisit</c>-th. The visit index is what makes specialisation expressible.
	/// </summary>
	readonly struct Transition {
		public Transition(Block from, int fromVisit, Block to, int toVisit, bool viaSite, int cut) {
			From = from; FromVisit = fromVisit; To = to; ToVisit = toVisit; ViaSite = viaSite; Cut = cut;
		}
		public Block From { get; }
		public int FromVisit { get; }
		public Block To { get; }
		public int ToVisit { get; }
		public bool ViaSite { get; }
		public int Cut { get; }
		public override string ToString() =>
			$"{XorSwitchTrace.Id(From)}#{FromVisit} -> {XorSwitchTrace.Id(To)}#{ToVisit} viaSite={ViaSite} cut={Cut}";
	}

	/// <summary>Why a walk stopped. Only <see cref="Exit"/> is a result worth applying.</summary>
	enum Outcome {
		Exit,               // ret / throw / rethrow reached -- the machine provably terminates
		Undetermined,       // a branch or dispatch index that is not a known constant
		NotOwned,                // a region block has a predecessor outside the region
		RegionTooLarge,
		TooManyCopies,
		GrowthCapExceeded,
		UncloneableTerminator,   // a copy would need edges or a conditional rebuilt
		BudgetExhausted,
		UncuttablePush,     // the state push is not a removable pure suffix
		LeftTheMachine,     // control reached something this model does not describe
	}

	/// <summary>
	///     The maximal single-entry region a block starts, or null when ownership cannot be proven.
	///     Sites are exits, not members. Owned means every member but the entry has all predecessors
	///     inside — the safety property for copying it, since a member reachable from outside would
	///     have its other callers stranded by, or redirected into, the copy. Payloads branch and
	///     merge internally, so this is a subgraph, never a chain.
	/// </summary>
	static HashSet<Block>? DiscoverOwnedRegion(Block entry, HashSet<Block> sites, out Outcome failure) {
		failure = Outcome.Exit;
		var region = new HashSet<Block> { entry };
		var queue = new Queue<Block>();
		queue.Enqueue(entry);

		while (queue.Count > 0) {
			foreach (var succ in queue.Dequeue().GetTargets()) {
				if (succ is null || sites.Contains(succ) || region.Contains(succ))
					continue;
				// A region may not straddle an exception-handler boundary: the copy would sit in a
				// different protected region and be covered by different handlers.
				if (succ.Parent != entry.Parent) {
					failure = Outcome.NotOwned;
					return null;
				}
				if (region.Count >= MaxRegionBlocks) {
					failure = Outcome.RegionTooLarge;
					return null;
				}
				region.Add(succ);
				queue.Enqueue(succ);
			}
		}

		foreach (var block in region) {
			if (block == entry)
				continue;
			foreach (var pred in block.Sources) {
				if (!region.Contains(pred)) {
					failure = Outcome.NotOwned;
					return null;
				}
			}
		}
		return region;
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

		var plan = new List<Transition>();
		var outcome = Walk(blocks, allBlocks[0], sites, plan, out int sitesUsed, trace);
		// Logged unconditionally. Printing it only on the failure path is what made an exception in
		// the APPLY phase look like one in the walk.
		if (trace)
			XorSwitchTrace.Log($"relational: outcome={outcome} sitesUsed={sitesUsed} steps={plan.Count}");
		if (outcome != Outcome.Exit || sitesUsed < MinDispatchSites || plan.Count == 0)
			return false;

		// ---- validate the whole plan; nothing is mutated above this line or below it until apply ----
		var rewritten = new HashSet<(Block, int)>();
		var copies = new HashSet<(Block, int)>();
		foreach (var step in plan) {
			if (step.FromVisit > 0) copies.Add((step.From, step.FromVisit));
			if (step.ToVisit > 0) copies.Add((step.To, step.ToVisit));
			if (!rewritten.Add((step.From, step.FromVisit)))
				return Reject(trace, outcome, "one instance would need two terminators", step);
			if (step.FromVisit > 0 && step.From.LastInstr.IsConditionalBranch())
				return Reject(trace, Outcome.UncloneableTerminator, "a copy would need a conditional rebuilt", step);
		}
		// A copy inherits no edges — a fresh Block has no successors even though its copied
		// instructions end in a branch — so one that is never a step's source would be emitted
		// malformed.
		foreach (var copy in copies) {
			if (!rewritten.Contains(copy))
				return Reject(trace, Outcome.UncloneableTerminator,
					$"a copy of {XorSwitchTrace.Id(copy.Item1)}#{copy.Item2} would get no edges", default);
		}
		foreach (var (block, _) in copies.ToList()) {
			var region = DiscoverOwnedRegion(block, sites, out var why);
			if (region is null)
				return Reject(trace, why, "single-entry ownership could not be proven", default);
		}
		int addedInstructions = copies.Sum(c => c.Item1.Instructions.Count);
		if (copies.Count > MaxSpecialisedBlocks || addedInstructions > MaxAddedInstructions)
			return Reject(trace, Outcome.GrowthCapExceeded,
				$"growth {copies.Count} blocks / {addedInstructions} instrs exceeds the cap", default);

		// ---- apply ----
		if (trace)
			XorSwitchTrace.Log($"relational: APPLYING {plan.Count} step(s), {copies.Count} copy/ies");
		var made = new Dictionary<(Block, int), Block>();

		// Every copy is taken FIRST, from the pristine blocks. Materialising lazily inside the rewrite
		// loop makes the loop read and write the same blocks in turn: an earlier step's
		// ReplaceLastInstrsWithBranch strips a block's instructions, and a later step then copies that
		// stripped block. The empty copy is handed a cut computed against the original, which throws.
		// The plan is validated as a whole; applying it has to be atomic with respect to its own
		// inputs too.
		foreach (var step in plan) {
			Materialise(step.From, step.FromVisit, made);
			Materialise(step.To, step.ToVisit, made);
		}

		foreach (var step in plan) {
			var from = Materialise(step.From, step.FromVisit, made);
			var to = Materialise(step.To, step.ToVisit, made);
			if (trace)
				XorSwitchTrace.Log($"  apply {step} (fromInstrs={from.Instructions.Count})");
			if (step.ViaSite)
				from.ReplaceLastInstrsWithBranch(step.Cut, to);
			else if (step.FromVisit > 0 || step.ToVisit > 0)
				from.ReplaceLastInstrsWithBranch(from.LastInstr.IsBr() ? 1 : 0, to);
		}

		if (trace)
			XorSwitchTrace.Log($"relational: APPLIED copies={made.Count} addedInstrs={addedInstructions}");
		Logger.v("  XOR-switch relational: resolved {0} step(s), {1} site(s), {2} specialised in {3}",
			plan.Count, sitesUsed, made.Count, blocks.Method?.Name ?? "?");
		return true;
	}

	static bool Reject(bool trace, Outcome outcome, string why, Transition step) {
		if (trace)
			XorSwitchTrace.Log($"relational: REFUSED ({outcome}) -- {why}"
				+ (step.From is null ? "" : $" [{step}]"));
		return false;
	}

	/// <summary>The block carrying a given visit: the original for the first, a copy for each later one.</summary>
	static Block Materialise(Block block, int visit, Dictionary<(Block, int), Block> made) {
		if (visit <= 0)
			return block;
		if (made.TryGetValue((block, visit), out var existing))
			return existing;
		var clone = new Block();
		foreach (var instr in block.Instructions) {
			// Fresh Instruction objects: two blocks sharing one instance would share its Offset, and
			// the offset is what the writer and every branch fixup key on.
			clone.Instructions.Add(new Instr(new Instruction(instr.OpCode, instr.Instruction.Operand)));
		}
		block.Parent!.Add(clone);
		made[(block, visit)] = clone;
		return clone;
	}

	/// <summary>
	///     Interpret the method forward from its first instruction, recording the edge each dispatch
	///     predecessor should become. Mutates nothing.
	/// </summary>
	static Outcome Walk(Blocks blocks, Block entry, HashSet<Block> sites, List<Transition> plan,
			out int sitesUsed, bool trace = false) {
		sitesUsed = 0;
		var usedSites = new HashSet<Block>();
		var visits = new Dictionary<Block, int>();

		var emu = new InstructionEmulator();
		// From the first instruction, so `.locals init` zeroing is real rather than assumed -- the
		// one place that claim is sound. See ROADMAP §7 item 4 for what assuming it elsewhere cost.
		emu.Initialize(blocks, true);

		var current = entry;
		Block? pendingPredecessor = null;
		int pendingPredecessorDepth = 0;
		int pendingPredecessorVisit = 0;

		for (int step = 0; step < MaxSteps; step++) {
			if (current is null)
				return Outcome.LeftTheMachine;

			int entryDepth = emu.StackSize();
			bool isSite = sites.Contains(current);
			// A dispatch site is re-entered every iteration by construction and carries no state of
			// its own, so it is never specialised. A payload block reached again is a distinct step
			// and gets its own copy; the visit index distinguishes them.
			int visit = 0;
			if (!isSite) {
				visits.TryGetValue(current, out visit);
				visits[current] = visit + 1;
				if (visit >= MaxRegionCopies)
					return Outcome.TooManyCopies;
				if (visits.Count > MaxVisitedBlocks)
					return Outcome.BudgetExhausted;
			}
			if (trace)
				XorSwitchTrace.Log($"  step {step}: {XorSwitchTrace.Id(current)}#{visit} site={isSite} "
					+ $"last={current.LastInstr.OpCode.Name} succ={current.GetTargets().Count()} "
					+ $"pred={current.Sources.Count} depth={emu.StackSize()} [{XorSwitchTrace.Sketch(current)}]");

			// Fill in the arriving side of the step that led here, now its visit index is known.
			if (plan.Count > 0 && plan[plan.Count - 1].ToVisit < 0) {
				var pending = plan[plan.Count - 1];
				plan[plan.Count - 1] = new Transition(pending.From, pending.FromVisit, pending.To,
					isSite ? 0 : visit, pending.ViaSite, pending.Cut);
			}

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

				plan.Add(new Transition(pendingPredecessor, pendingPredecessorVisit, target, -1, true, cut));
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

			// Only genuinely internal moves. A move INTO a site is that site's traversal, recorded
			// on arrival; recording it here too would read as two terminators for one instance.
			if (!sites.Contains(next))
				plan.Add(new Transition(current, visit, next, -1, false, 0));
			pendingPredecessor = current;
			pendingPredecessorVisit = visit;
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
