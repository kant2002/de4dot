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
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4.xorswitch;

/// <summary>
///     Deobfuscates XOR-switch state machines produced by .NET Reactor v6.x.
///     Unified CFG-driven approach using abstract interpretation instead of pattern matching.
/// </summary>
class XorSwitchDeobfuscator : IBlocksDeobfuscator, ISwitchDispatchResolver {
	Blocks _blocks;

	public bool ExecuteIfNotModified => false;

	/// <summary>
	///     Detect, fold and resolve exactly as usual, but do not rewire anything. The unresolved
	///     sibling candidate then differs from the resolved one by the redirect alone, which is the
	///     decision the state-machine trace is being asked to judge.
	/// </summary>
	public bool SuppressDispatchResolution { get; set; }

	public void DeobfuscateBegin(Blocks blocks) => this._blocks = blocks;



	public bool Deobfuscate(List<Block> allBlocks) {
		if (XorSwitchTrace.Wants(_blocks.Method))
			XorSwitchTrace.BeginMethod(_blocks.Method!);
		bool modified = false;
		int totalDispatches = 0;
		int totalResolved = 0;
		int totalFailed = 0;
		int totalApplied = 0;
		int totalDead = 0;
		foreach (var block in allBlocks) {
			if (block.LastInstr.OpCode.Code != Code.Switch)
				continue;

			// Preprocessing: fold opaque constants
			OpaquePredicateFixer.Fold(block);
			foreach (var source in new List<Block>(block.Sources)) {
				if (source != block)
					OpaquePredicateFixer.Fold(source);
			}

			// Detect dispatch
			var dispatch = DispatchDetector.TryDetect(block, _blocks);
			if (dispatch is null)
				continue;

			totalDispatches++;
			var node = dispatch.Value;

			// Also fold opaque constants in header block sources
			if (node.HeaderBlock is not null) {
				foreach (var source in node.HeaderBlock.Sources) {
					if (source != block && source != node.HeaderBlock)
						OpaquePredicateFixer.Fold(source);
				}
			}

			// Resolve edges
			var resolver = new EdgeResolver(node, _blocks);
			var edges = resolver.ResolveAll();
			totalResolved += resolver.ResolvedCount;
			totalFailed += resolver.FailedCount;

			if (edges.Count == 0 || SuppressDispatchResolution)
				continue;

			// Apply resolved edges
			int applied = SwitchRewriter.Apply(_blocks, node, edges);
			totalApplied += applied;


			if (applied > 0)
				modified = true;

			// Count dead cases
			totalDead += node.CaseTargets.Count(caseTarget => caseTarget.Sources.Count == 0);
		}

		if (totalDispatches > 0)
			Logger.v("  XOR-switch [{5}]: {0} dispatches, {1} edges resolved, {2} failed, {3} applied, {4} dead cases",
				totalDispatches, totalResolved, totalFailed, totalApplied, totalDead, _blocks.Method?.Name ?? "?");


		return modified;
	}
}
