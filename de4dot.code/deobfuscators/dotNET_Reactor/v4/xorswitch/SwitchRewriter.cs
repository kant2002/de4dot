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

/// <summary>
///     Applies resolved edges to the CFG. Pure transformation.
/// </summary>
static class SwitchRewriter {
	/// <summary>
	///     Would applying <paramref name="edges"/> leave the method with no reachable ret/throw?
	///
	///     This happens when only some of a dispatch's cases resolve. Each applied edge redirects a
	///     predecessor away from the switch block; once the last live predecessor is redirected, the
	///     switch itself becomes unreachable, and so does every case that was NOT resolved -- which
	///     can include the one holding the method's only exit. The blocks still exist at that point,
	///     so nothing here notices, but de4dot's later dead-block cleanup removes them and the method
	///     is left looping forever. That is verifiable IL, so ilverify cannot catch it either.
	///
	///     Simulated on a copy of the successor map, so this is read-only: if the answer is "yes",
	///     the caller leaves the dispatch alone and the switch survives as a recoverable goto, which
	///     is always better than a silent infinite loop.
	/// </summary>
	static bool WouldOrphanMethodExit(Blocks blocks, List<ResolvedEdge> edges) {
		var all = blocks.MethodBlocks.GetAllBlocks();
		if (all.Count == 0)
			return false;

		// successor map with the pending redirects already applied
		var succ = new Dictionary<Block, List<Block>>();
		foreach (var b in all) {
			var list = new List<Block>();
			if (b.FallThrough is not null)
				list.Add(b.FallThrough);
			if (b.Targets is not null) {
				foreach (var t in b.Targets)
					if (t is not null)
						list.Add(t);
			}
			succ[b] = list;
		}
		foreach (var e in edges) {
			if (e.Predecessor is null || e.Target is null)
				continue;
			if (e.Predecessor == e.Target || e.Predecessor.Parent != e.Target.Parent)
				continue; // Apply() skips these, so the simulation must too
			succ[e.Predecessor] = new List<Block> { e.Target };
		}

		var seen = new HashSet<Block>();
		var stack = new Stack<Block>();
		stack.Push(all[0]);
		while (stack.Count > 0) {
			var b = stack.Pop();
			if (!seen.Add(b))
				continue;
			foreach (var instr in b.Instructions) {
				switch (instr.OpCode.Code) {
				case Code.Ret:
				case Code.Throw:
				case Code.Rethrow:
					return false; // an exit is still reachable
				}
			}
			if (succ.TryGetValue(b, out var next)) {
				foreach (var n in next)
					stack.Push(n);
			}
		}
		return true;
	}

	public static int Apply(Blocks blocks, DispatchNode dispatch, List<ResolvedEdge> edges) {
		if (WouldOrphanMethodExit(blocks, edges)) {
			Logger.v("  XOR-switch: skipping {0} edge(s) in {1} — the rewrite would orphan the "
				+ "method's only exit (unresolved cases reachable only through this switch)",
				edges.Count, blocks.Method?.Name ?? "?");
			return 0;
		}
		return Apply(dispatch, edges);
	}

	public static int Apply(DispatchNode dispatch, List<ResolvedEdge> edges) {
		int applied = 0;

		foreach (var edge in edges) {
			// Self-loop guard: never redirect a block to itself. A basic block has no
			// internal branch, so retaining some payload does not help — `payload; br self`
			// still loops forever. Leaving the edge unresolved yields a recoverable goto
			// instead of a bogus infinite loop.
			if (edge.Target == edge.Predecessor)
				continue;

			// Scope check: predecessor and target must be in the same exception handler scope
			if (edge.Predecessor.Parent != edge.Target.Parent)
				continue;

			// Noop check: skip if predecessor already branches directly to target
			if (AlreadyBranchesTo(edge.Predecessor, edge.Target))
				continue;

			// For conditional predecessors (InstructionsToRemove == 0), retarget the
			// edge that goes to the dispatch block (switch or header)
			if (edge.InstructionsToRemove == 0 && edge.Predecessor.IsConditionalBranch()) {
				if (RetargetConditionalEdge(edge.Predecessor, dispatch, edge.Target))
					applied++;
				continue;
			}

			// Standard rewrite: replace tail instructions with branch to target
			try {
				edge.Predecessor.ReplaceLastInstrsWithBranch(edge.InstructionsToRemove, edge.Target);
			}
			catch {
				continue;
			}

			// Insert pop instructions for stack cleanup
			for (int i = 0; i < edge.StackCleanupPops; i++)
				edge.Predecessor.Instructions.Add(new Instr(OpCodes.Pop.ToInstruction()));

			applied++;
		}

		// Clean up dead cases after rewriting
		CleanupDeadCases(dispatch, edges);

		return applied;
	}

	/// <summary>
	///     True if the block ends the method (ret/throw/rethrow). Such a block must never be
	///     discarded as "dead": doing so can leave the method with no exit at all, which is not
	///     type-unsafe (so ilverify will not catch it) but is an infinite loop at runtime.
	/// </summary>
	static bool IsMethodExit(Block block) {
		foreach (var instr in block.Instructions) {
			switch (instr.OpCode.Code) {
			case Code.Ret:
			case Code.Throw:
			case Code.Rethrow:
				return true;
			}
		}
		return false;
	}

	static bool AlreadyBranchesTo(Block block, Block target) {
		var onlyTarget = block.GetOnlyTarget();
		return onlyTarget == target;
	}

	/// <summary>
	///     Retarget a conditional branch edge from a dispatch block to the resolved target.
	///     Checks both SwitchBlock and HeaderBlock.
	/// </summary>
	static bool RetargetConditionalEdge(Block predecessor, DispatchNode dispatch, Block target) {
		// Check fallthrough against both dispatch blocks
		if (predecessor.FallThrough == dispatch.SwitchBlock ||
			(dispatch.HeaderBlock is not null && predecessor.FallThrough == dispatch.HeaderBlock)) {
			predecessor.SetNewFallThrough(target);
			return true;
		}

		// Check explicit targets against both dispatch blocks
		if (predecessor.Targets is not null) {
			for (int i = 0; i < predecessor.Targets.Count; i++) {
				if (predecessor.Targets[i] == dispatch.SwitchBlock ||
					(dispatch.HeaderBlock is not null && predecessor.Targets[i] == dispatch.HeaderBlock)) {
					predecessor.SetNewTarget(i, target);
					return true;
				}
			}
		}

		return false;
	}

	/// <summary>
	///     After rewriting, check each case target and the header block.
	///     If a block has no remaining sources, it's dead and can be removed.
	/// </summary>
	static void CleanupDeadCases(DispatchNode dispatch, List<ResolvedEdge> edges) {
		if (edges.Count == 0)
			return;

		foreach (var caseTarget in dispatch.CaseTargets) {
			if (caseTarget.Sources.Count == 0 && caseTarget.Parent is not null) {
				// Never discard a block that ends the method: doing so can leave the method
				// with no exit at all -- verifiable IL, but an infinite loop at runtime.
				if (IsMethodExit(caseTarget))
					continue;
				try {
					caseTarget.Parent.RemoveGuaranteedDeadBlock(caseTarget);
				}
				catch {
					// Block may not be in the parent's baseBlocks list
				}
			}
		}

		// Also clean up the header block if it has no remaining sources
		if (dispatch.HeaderBlock is { Sources.Count: 0, Parent: not null }) {
			try {
				dispatch.HeaderBlock.Parent.RemoveGuaranteedDeadBlock(dispatch.HeaderBlock);
			}
			catch {
				// Block may not be in the parent's baseBlocks list
			}
		}
	}
}
