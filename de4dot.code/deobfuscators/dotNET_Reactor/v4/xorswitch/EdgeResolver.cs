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

struct ResolvedEdge {
	public Block Predecessor { get; init; }
	public Block Target { get; init; }
	public int CaseIndex { get; init; }
	public int InstructionsToRemove { get; init; }
	public int StackCleanupPops { get; init; }
	public int? TargetIncomingStateVar { get; init; }
}

/// <summary>
///     For each predecessor of the switch block, uses InstructionEmulator to determine
///     which case it dispatches to. Five resolution phases:
///     1. Direct resolution (entry blocks that set stateVar = constant)
///     2. Seeded forward propagation (case-to-case using known stateVar values)
///     3. Brute-force seeding (disconnected subgraphs, try all known seeds)
///     4. Indirect resolution through passthrough blocks (e.g., pop blocks)
///     5. Algebraic seed extraction (compute next-case seeds from instruction constants)
///     Phases 2-5 are wrapped in a fixed-point loop that iterates until no new edges
///     or seeds are discovered.
/// </summary>
class EdgeResolver {
	readonly DispatchNode _dispatch;
	readonly Blocks _blocks;
	readonly MethodDef _method;

	public int ResolvedCount { get; private set; }

	public int FailedCount { get; private set; }

	public EdgeResolver(DispatchNode dispatch, Blocks blocks) {
		_dispatch = dispatch;
		_blocks = blocks;
		_method = blocks.Method;
	}

	/// <summary>
	///     Creates and initializes an emulator, seeding stateVar from the provided value,
	///     zero (for entry blocks with no predecessors), or unknown.
	/// </summary>
	InstructionEmulator CreateEmulator(int? seedStateVar, bool isEntry) {
		var emu = new InstructionEmulator();
		emu.Initialize(_method, false);
		if (_dispatch.StateVar is not null) {
			if (seedStateVar.HasValue)
				emu.SetLocal(_dispatch.StateVar, new Int32Value(seedStateVar.Value));
			else if (isEntry)
				emu.SetLocal(_dispatch.StateVar, Int32Value.Zero);
			else
				emu.SetLocal(_dispatch.StateVar, Int32Value.CreateUnknown());
		}
		return emu;
	}

	/// <summary>
	///     Returns all predecessors of the dispatch (from both SwitchBlock and HeaderBlock).
	///     Excludes the SwitchBlock and HeaderBlock themselves.
	/// </summary>
	List<Block> GetDispatchPredecessors() {
		var seen = new HashSet<Block> { _dispatch.SwitchBlock };
		if (_dispatch.HeaderBlock is not null)
			seen.Add(_dispatch.HeaderBlock);

		var result = _dispatch.SwitchBlock.Sources.Where(pred => seen.Add(pred)).ToList();
		
		if (_dispatch.HeaderBlock is not null) {
			result.AddRange(_dispatch.HeaderBlock.Sources.Where(pred => seen.Add(pred)));
		}
		
		return result;
	}

	/// <summary>
	///     Checks if a block is part of the dispatch (either the switch block or header block).
	/// </summary>
	bool IsDispatchBlock(Block block) => block == _dispatch.SwitchBlock || block == _dispatch.HeaderBlock;

	/// <summary>
	///     Emulates the dispatch blocks (header + switch computation, excluding the switch itself).
	///     Handles both the split case (header + switch-only) and the unified case.
	/// </summary>
	void EmulateDispatchBlocks(InstructionEmulator emu, Block predecessor, out Int32Value preRemDividend) {
		preRemDividend = null;

		// Emulate header block if present and the predecessor targets it
		if (_dispatch.HeaderBlock is not null && _dispatch.HeaderBlock.Sources.Contains(predecessor)) {
			var hdrInstrs = _dispatch.HeaderBlock.Instructions;
			int hdrEnd = hdrInstrs.Count;
			if (hdrEnd > 0 && hdrInstrs[hdrEnd - 1].IsBr())
				hdrEnd--;
			emu.Emulate(hdrInstrs, 0, hdrEnd);
		}

		// Split switch block emulation at rem.un to capture pre-rem dividend
		var switchInstrs = _dispatch.SwitchBlock.Instructions;
		int switchIdx = FindSwitchIndex(switchInstrs);
		if (switchIdx < 0)
			return;

		int remIdx = FindRemUnIndex(switchInstrs);
		int emuEnd = switchIdx;

		if (remIdx > 0 && remIdx < emuEnd) {
			emu.Emulate(switchInstrs, 0, remIdx);
			if (emu.StackSize() >= 2) {
				var divisor = emu.Pop();
				if (emu.Peek() is Int32Value dv)
					preRemDividend = dv;
				emu.Push(divisor);
				if (divisor is Int32Value divIv && divIv.AllBitsValid() &&
					(uint)divIv.Value != (uint)_dispatch.CaseTargets.Count)
					preRemDividend = null;
			}
			emu.Emulate(switchInstrs, remIdx, emuEnd);
		}
		else if (emuEnd > 0) {
			emu.Emulate(switchInstrs, 0, emuEnd);
		}
	}

	public List<ResolvedEdge> ResolveAll() {
		var edges = new List<ResolvedEdge>();
		var resolved = new HashSet<Block>();

		// Phase 1: Direct resolution (unseeded)
		int maxIterations = _dispatch.CaseTargets.Count * 4;
		for (int iter = 0; iter < maxIterations; iter++) {
			bool foundNew = false;

			foreach (var pred in GetDispatchPredecessors()) {
				if (resolved.Contains(pred))
					continue;
				
				if (pred.LastInstr.OpCode.Code == Code.Switch)
					continue;

				if (IsUnconditionalPredecessor(pred)) {
					var edge = TryResolveEdge(pred);
					if (edge is null) continue;
					
					edges.Add(edge.Value);
					resolved.Add(pred);
					ResolvedCount++;
					foundNew = true;
					continue;
				}
				
				if (IsConditionalPredecessor(pred)) {
					var condEdges = TryResolveConditionalEdge(pred);
					if (condEdges is not { Count: > 0 }) continue;
					
					edges.AddRange(condEdges);
					resolved.Add(pred);
					ResolvedCount += condEdges.Count;
					foundNew = true;
					continue;
				}
			}

			if (!foundNew)
				break;
		}

		var indirectResolved = new HashSet<Block>();
		Dictionary<int, int> caseStateVar = null;
		HashSet<int> allSeeds = null;

		// Phases 2-5 with fixed-point iteration (requires stateVar)
		if (_dispatch.StateVar is not null) {
			caseStateVar = new Dictionary<int, int>();
			foreach (var edge in edges.Where(edge => edge.TargetIncomingStateVar.HasValue))
			{
				caseStateVar[edge.CaseIndex] = edge.TargetIncomingStateVar.Value;
			}
			allSeeds = new HashSet<int>(caseStateVar.Values);

			int maxOuterIter = _dispatch.CaseTargets.Count * 3;
			for (int outerIter = 0; outerIter < maxOuterIter; outerIter++) {
				int prevEdgeCount = edges.Count;
				int prevSeedCount = allSeeds.Count;

				// Phase 2: Seeded forward propagation
				{
					int maxPhase2 = _dispatch.CaseTargets.Count * 2;
					for (int iter = 0; iter < maxPhase2; iter++) {
						bool foundNew2 = false;

						foreach (var pred in GetDispatchPredecessors()) {
							if (resolved.Contains(pred))
								continue;
							if (pred.LastInstr.OpCode.Code == Code.Switch)
								continue;
							if (!IsUnconditionalPredecessor(pred))
								continue;

							if (!_dispatch.BlockToCase.TryGetValue(pred, out int predCaseIdx))
								continue;
							if (!caseStateVar.TryGetValue(predCaseIdx, out int seed))
								continue;

							var edge = TryResolveEdge(pred, seed);
							if (edge is not null) {
								edges.Add(edge.Value);
								resolved.Add(pred);
								ResolvedCount++;
								foundNew2 = true;

								if (edge.Value.TargetIncomingStateVar.HasValue) {
									caseStateVar[edge.Value.CaseIndex] = edge.Value.TargetIncomingStateVar.Value;
									allSeeds.Add(edge.Value.TargetIncomingStateVar.Value);
								}
							}
						}

						if (!foundNew2)
							break;
					}
				}

				// Phase 3: Brute-force all known seeds for disconnected subgraphs
				if (allSeeds.Count > 0) {
					bool phase3Progress = true;
					int maxPhase3 = _dispatch.CaseTargets.Count * 2;
					for (int iter3 = 0; iter3 < maxPhase3 && phase3Progress; iter3++) {
						phase3Progress = false;

						foreach (var pred in GetDispatchPredecessors()) {
							if (resolved.Contains(pred))
								continue;
							if (pred.LastInstr.OpCode.Code == Code.Switch)
								continue;
							if (!IsUnconditionalPredecessor(pred))
								continue;

							bool hasCaseIdx = _dispatch.BlockToCase.TryGetValue(pred, out int ci);
							if (hasCaseIdx && caseStateVar.ContainsKey(ci))
								continue;

							foreach (var trySeed in allSeeds) {
								if (hasCaseIdx && !VerifySeedRoutesToCase(trySeed, ci))
									continue;
								var edge = TryResolveEdge(pred, trySeed);
								if (edge is not null) {
									edges.Add(edge.Value);
									resolved.Add(pred);
									ResolvedCount++;
									phase3Progress = true;

									if (edge.Value.TargetIncomingStateVar.HasValue) {
										int newSeed = edge.Value.TargetIncomingStateVar.Value;
										caseStateVar[edge.Value.CaseIndex] = newSeed;
										allSeeds.Add(newSeed);
									}
									break;
								}
							}
						}
					}
				}

				// Phase 4: Indirect resolution through passthrough blocks
				{
					bool progress4 = true;
					int maxIter4 = _dispatch.CaseTargets.Count * 2;
					for (int iter4 = 0; iter4 < maxIter4 && progress4; iter4++) {
						progress4 = false;

						foreach (var passthrough in GetDispatchPredecessors()) {
							if (resolved.Contains(passthrough))
								continue;
							if (passthrough.LastInstr.OpCode.Code == Code.Switch)
								continue;
							if (!IsUnconditionalPredecessor(passthrough))
								continue;

							bool hasPtCaseIdx = _dispatch.BlockToCase.TryGetValue(passthrough, out int ptCaseIdx);

							foreach (var src in new List<Block>(passthrough.Sources)) {
								if (indirectResolved.Contains(src))
									continue;
								if (IsDispatchBlock(src))
									continue;
								if (src.LastInstr.OpCode.Code == Code.Switch)
									continue;

								var intermediates = new List<Block> { passthrough };
								var edge = TryResolveIndirectEdge(src, intermediates);

								if (edge is null) {
									IEnumerable<int> seeds = allSeeds;

									if (hasPtCaseIdx) {
										seeds = seeds.Where(seed => VerifySeedRoutesToCase(seed, ptCaseIdx));
									}

									edge = seeds.Select(seed => TryResolveIndirectEdge(src, intermediates, seed)).FirstOrDefault();
								}

								if (edge is not null) {
									edges.Add(edge.Value);
									indirectResolved.Add(src);
									ResolvedCount++;
									progress4 = true;

									// Immediately merge Phase 4 seeds
									if (edge.Value.TargetIncomingStateVar.HasValue) {
										int newSeed = edge.Value.TargetIncomingStateVar.Value;
										caseStateVar[edge.Value.CaseIndex] = newSeed;
										allSeeds.Add(newSeed);
									}
								}
							}
						}
					}
				}

				// Phase 5: Algebraic seed extraction
				RunAlgebraicSeedExtraction(caseStateVar, allSeeds);

				// Phase 6: Forward propagation within cases
				// For unresolved blocks at a case with known seed, trace stateVar
				// through the case's code to find the actual value at the block
				{
					foreach (var pred in GetDispatchPredecessors()) {
						if (resolved.Contains(pred))
							continue;
						if (pred.LastInstr.OpCode.Code == Code.Switch)
							continue;
						if (!IsUnconditionalPredecessor(pred))
							continue;

						if (!_dispatch.BlockToCase.TryGetValue(pred, out int predCaseIdx))
							continue;
						if (!caseStateVar.TryGetValue(predCaseIdx, out int caseSeed))
							continue;

						// The case seed didn't work in Phase 2, meaning stateVar was
						// modified within the case. Trace forward from the case target
						// to find the actual stateVar value at this block.
						var derivedSeed = TraceStateVarForward(
							_dispatch.CaseTargets[predCaseIdx], pred, caseSeed);
						if (derivedSeed is null)
							continue;

						var edge = TryResolveEdge(pred, derivedSeed.Value, seedIsAtPredecessorEntry: true);
						if (edge is not null) {
							edges.Add(edge.Value);
							resolved.Add(pred);
							ResolvedCount++;

							if (edge.Value.TargetIncomingStateVar.HasValue) {
								int newSeed = edge.Value.TargetIncomingStateVar.Value;
								caseStateVar[edge.Value.CaseIndex] = newSeed;
								allSeeds.Add(newSeed);
							}
						}
					}
				}

				// Break if neither edges nor seeds grew
				if (edges.Count == prevEdgeCount && allSeeds.Count == prevSeedCount)
					break;
			}
		}
		else {
			// No stateVar: Phase 4 only (unseeded indirect resolution)
			bool progress4 = true;
			int maxIter4 = _dispatch.CaseTargets.Count * 2;
			for (int iter4 = 0; iter4 < maxIter4 && progress4; iter4++) {
				progress4 = false;

				foreach (var passthrough in GetDispatchPredecessors()) {
					if (resolved.Contains(passthrough))
						continue;
					if (passthrough.LastInstr.OpCode.Code == Code.Switch)
						continue;
					if (!IsUnconditionalPredecessor(passthrough))
						continue;

					foreach (var src in new List<Block>(passthrough.Sources)) {
						if (indirectResolved.Contains(src))
							continue;
						if (IsDispatchBlock(src))
							continue;
						if (src.LastInstr.OpCode.Code == Code.Switch)
							continue;

						var intermediates = new List<Block> { passthrough };
						var edge = TryResolveIndirectEdge(src, intermediates);

						if (edge is not null) {
							edges.Add(edge.Value);
							indirectResolved.Add(src);
							ResolvedCount++;
							progress4 = true;
						}
					}
				}
			}
		}

		// Mark passthrough blocks as resolved if all their sources were handled
		foreach (var passthrough in GetDispatchPredecessors()) {
			if (resolved.Contains(passthrough))
				continue;
			if (passthrough.Sources.Any(src => indirectResolved.Contains(src) || resolved.Contains(src)))
				resolved.Add(passthrough);
		}

		// Compute accurate unresolved count
		FailedCount = 0;
		foreach (var pred in GetDispatchPredecessors()) {
			if (resolved.Contains(pred))
				continue;
			if (pred.LastInstr.OpCode.Code == Code.Switch)
				continue;
			FailedCount++;
		}

		return edges;
	}

	bool IsUnconditionalPredecessor(Block block) {
		var lastInstr = block.LastInstr;
		return lastInstr.IsBr() || block.IsFallThrough();
	}

	bool IsConditionalPredecessor(Block block) {
		if (!block.IsConditionalBranch())
			return false;
		if (IsDispatchBlock(block.FallThrough))
			return true;
		return block.Targets?.Any(IsDispatchBlock) ?? false;
	}

	/// <summary>
	///     Builds a chain of blocks to emulate by walking backward from the predecessor
	///     through single-predecessor fall-through chains.
	/// </summary>
	List<Block> BuildEmulationChain(Block predecessor) {
		var chain = new List<Block> { predecessor };
		var visited = new HashSet<Block> { predecessor, _dispatch.SwitchBlock };
		if (_dispatch.HeaderBlock is not null)
			visited.Add(_dispatch.HeaderBlock);
		var current = predecessor;
		const int maxChainLength = 20;

		while (chain.Count < maxChainLength) {
			Block singlePred = null;
			int predCount = 0;
			foreach (var src in current.Sources) {
				if (visited.Contains(src))
					continue;
				if (src.LastInstr.OpCode.Code == Code.Switch)
					continue;
				if (src.FallThrough == current || (src.Targets is not null && src.Targets.Count == 1 && src.Targets[0] == current && src.LastInstr.IsBr())) {
					singlePred = src;
					predCount++;
				}
				else {
					predCount++;
				}
			}

			if (predCount != 1 || singlePred is null)
				break;

			chain.Add(singlePred);
			visited.Add(singlePred);
			current = singlePred;
		}

		chain.Reverse();
		return chain;
	}

	ResolvedEdge? TryResolveEdge(Block predecessor, int? seedStateVar = null, bool seedIsAtPredecessorEntry = false) {
		var predInstrs = predecessor.Instructions;

		// When the caller already knows the stateVar value at the predecessor's own entry
		// (e.g. Phase 6's forward trace from the case target), emulate only the predecessor.
		// Building the backward chain here would re-emulate the preceding blocks and apply
		// their stateVar updates a second time on top of the already-derived seed, producing
		// an in-range but wrong case index.
		var chain = seedIsAtPredecessorEntry
			? new List<Block> { predecessor }
			: BuildEmulationChain(predecessor);

		var emu = CreateEmulator(seedStateVar, chain[0].Sources.Count == 0);

		Int32Value preRemDividend = null;

		try {
			foreach (var block in chain) {
				var instrs = block.Instructions;
				int end = instrs.Count;
				if (end > 0 && instrs[end - 1].IsBr())
					end--;
				emu.Emulate(instrs, 0, end);
			}

			EmulateDispatchBlocks(emu, predecessor, out preRemDividend);
		}
		catch {
			return null;
		}

		if (emu.StackSize() < 1)
			return null;

		var tos = emu.Pop();
		int caseIndex;

		if (tos is Int32Value iv && iv.AllBitsValid()) {
			caseIndex = iv.Value; // standard path — already the unsigned remainder
		}
		else if (preRemDividend is not null) {
			var partial = TryPartialCaseIndex(preRemDividend);
			if (partial is null)
				return null;
			caseIndex = partial.Value;
		}
		else {
			return null;
		}

		if (caseIndex < 0 || caseIndex >= _dispatch.CaseTargets.Count)
			return null;

		var target = _dispatch.CaseTargets[caseIndex];

		int? targetIncoming = null;
		if (_dispatch.StateVar is not null) {
			var sv = emu.GetLocal(_dispatch.StateVar);
			if (sv is Int32Value svIv && svIv.AllBitsValid())
				targetIncoming = svIv.Value;
		}

		var boundary = StateUpdateFinder.Find(predecessor, _dispatch, _blocks.Locals);
		int instrsToRemove;
		int stackPops;

		if (boundary.Found) {
			instrsToRemove = predInstrs.Count - boundary.CutPoint;
			stackPops = boundary.StackDepthAtCut;
		}
		else {
			instrsToRemove = predInstrs[predInstrs.Count - 1].IsBr() ? 1 : 0;
			stackPops = 0;
		}

		return new ResolvedEdge {
			Predecessor = predecessor,
			Target = target,
			CaseIndex = caseIndex,
			InstructionsToRemove = instrsToRemove,
			StackCleanupPops = stackPops,
			TargetIncomingStateVar = targetIncoming,
		};
	}

	List<ResolvedEdge> TryResolveConditionalEdge(Block predecessor) {
		var predInstrs = predecessor.Instructions;
		var edges = new List<ResolvedEdge>();

		int branchIdx = predInstrs.Count - 1;
		if (branchIdx < 0 || !predInstrs[branchIdx].IsConditionalBranch())
			return null;

		var fallThrough = predecessor.FallThrough;
		Block branchTarget = null;
		if (predecessor.Targets is { Count: 1 })
			branchTarget = predecessor.Targets[0];

		bool fallThroughToSwitch = IsDispatchBlock(fallThrough);
		bool branchToSwitch = branchTarget is not null && IsDispatchBlock(branchTarget);

		if (!fallThroughToSwitch && !branchToSwitch)
			return null;

		var edge = TryResolveConditionalPath(predecessor);
		if (edge is not null) {
			// Add once per path that leads to the switch so Apply retargets each one
			if (fallThroughToSwitch)
				edges.Add(edge.Value);
			if (branchToSwitch)
				edges.Add(edge.Value);
		}

		return edges;
	}

	ResolvedEdge? TryResolveConditionalPath(Block predecessor) {
		var predInstrs = predecessor.Instructions;

		var emu = CreateEmulator(null, false);

		try {
			emu.Emulate(predInstrs, 0, predInstrs.Count);
			EmulateDispatchBlocks(emu, predecessor, out _);
		}
		catch {
			return null;
		}

		if (emu.StackSize() < 1)
			return null;

		var tos = emu.Pop();
		if (tos is not Int32Value iv || !iv.AllBitsValid())
			return null;

		int caseIndex = iv.Value;
		if (caseIndex < 0 || caseIndex >= _dispatch.CaseTargets.Count)
			return null;

		return new ResolvedEdge {
			Predecessor = predecessor,
			Target = _dispatch.CaseTargets[caseIndex],
			CaseIndex = caseIndex,
			InstructionsToRemove = 0,
			StackCleanupPops = 0,
		};
	}

	/// <summary>
	///     Resolves an indirect predecessor that reaches the switch through intermediate
	///     (passthrough) blocks. Emulates [source, intermediates..., dispatch].
	/// </summary>
	ResolvedEdge? TryResolveIndirectEdge(Block source, List<Block> intermediates, int? seedStateVar = null) {
		var srcInstrs = source.Instructions;

		// Compute exit stack depth of source block to determine cleanup pops needed
		var depths = StateUpdateFinder.ComputeStackDepths(srcInstrs);
		if (depths is null)
			return null;

		int exitDepth = depths[srcInstrs.Count];

		var emu = CreateEmulator(seedStateVar, source.Sources.Count == 0);

		Int32Value preRemDividend = null;

		try {
			int srcEnd = srcInstrs.Count;
			if (srcEnd > 0 && srcInstrs[srcEnd - 1].IsBr())
				srcEnd--;

			emu.Emulate(srcInstrs, 0, srcEnd);

			foreach (var mid in intermediates) {
				var midInstrs = mid.Instructions;
				int midEnd = midInstrs.Count;
				if (midEnd > 0 && midInstrs[midEnd - 1].IsBr())
					midEnd--;
				emu.Emulate(midInstrs, 0, midEnd);
			}

			// The last intermediate determines the dispatch entry point
			var lastMid = intermediates[intermediates.Count - 1];
			EmulateDispatchBlocks(emu, lastMid, out preRemDividend);
		}
		catch {
			return null;
		}

		if (emu.StackSize() < 1)
			return null;

		var tos = emu.Pop();
		int caseIndex;

		if (tos is Int32Value iv && iv.AllBitsValid()) {
			caseIndex = iv.Value;
		}
		else if (preRemDividend is not null) {
			var partial = TryPartialCaseIndex(preRemDividend);
			if (partial is null)
				return null;
			caseIndex = partial.Value;
		}
		else {
			return null;
		}

		if (caseIndex < 0 || caseIndex >= _dispatch.CaseTargets.Count)
			return null;

		var target = _dispatch.CaseTargets[caseIndex];

		int? targetIncoming = null;
		if (_dispatch.StateVar is not null) {
			var sv = emu.GetLocal(_dispatch.StateVar);
			if (sv is Int32Value svIv && svIv.AllBitsValid())
				targetIncoming = svIv.Value;
		}

		if (source.Parent != target.Parent)
			return null;

		int instrsToRemove = (srcInstrs.Count > 0 && srcInstrs[srcInstrs.Count - 1].IsBr()) ? 1 : 0;

		return new ResolvedEdge {
			Predecessor = source,
			Target = target,
			CaseIndex = caseIndex,
			InstructionsToRemove = instrsToRemove,
			StackCleanupPops = exitDepth,
			TargetIncomingStateVar = targetIncoming,
		};
	}

	// --- Phase 6: Forward stateVar tracing ---

	/// <summary>
	///     Traces the stateVar value forward from a case target to a specific
	///     dispatch predecessor block using block-by-block isolated analysis.
	///     For each block in the path: tries algebraic extraction first, then
	///     falls back to isolated emulation (which is resilient to calls/field
	///     loads that produce unknowns on the stack but don't affect locals).
	///     Returns the stateVar value at the predecessor's entry, or null if
	///     the path can't be found or stateVar becomes undeterminable.
	/// </summary>
	int? TraceStateVarForward(Block caseTarget, Block targetPred, int caseSeed) {
		if (_dispatch.StateVar is null)
			return null;

		var path = FindSimplePath(caseTarget, targetPred);
		if (path is null)
			return null;

		int currentSeed = caseSeed;

		// Track stateVar block-by-block through the path (excluding targetPred)
		for (int i = 0; i < path.Count - 1; i++) {
			var block = path[i];

			// Fast check: if block doesn't store to stateVar, skip it entirely
			if (!BlockWritesToLocal(block, _dispatch.StateVar))
				continue;

			// Try algebraic extraction (handles: ldloc V_7; ldc A; mul; ldc B; xor)
			var affine = ExtractAffineUpdate(block, _dispatch.StateVar, _blocks.Locals);
			if (affine is not null) {
				var (mulConst, xorConst) = affine.Value;
				currentSeed = unchecked((int)(((uint)currentSeed * (uint)mulConst) ^ (uint)xorConst));
				continue;
			}

			// Fallback: isolated emulation of this single block.
			// This handles non-standard patterns (e.g., ldc C; stloc V_7, or
			// ldloc V_7; ldc A; xor without mul) as long as the state update
			// only depends on stateVar and constants (not stack values from
			// previous blocks, which would appear as unknowns).
			var newSeed = EmulateBlockForStateVar(block, currentSeed);
			if (newSeed is not null) {
				currentSeed = newSeed.Value;
				continue;
			}

			// Can't determine stateVar after this block
			return null;
		}

		return currentSeed;
	}

	/// <summary>
	///     Checks if a block contains any stloc instruction targeting the given local.
	/// </summary>
	bool BlockWritesToLocal(Block block, Local local) {
		foreach (var instr in block.Instructions) {
			if (instr.IsStloc() && Instr.GetLocalVar(_blocks.Locals, instr) == local)
				return true;
		}
		return false;
	}

	/// <summary>
	///     Emulates a single block in isolation to determine the stateVar value after it.
	///     Starts with a fresh emulator seeded with the current stateVar value and an
	///     empty stack. This is resilient to calls/field loads in the block because
	///     they produce unknowns only on the stack, not in locals. Only fails when
	///     the state update depends on stack values from previous blocks.
	/// </summary>
	int? EmulateBlockForStateVar(Block block, int currentSeed) {
		var emu = new InstructionEmulator();
		emu.Initialize(_method, false);
		emu.SetLocal(_dispatch.StateVar, new Int32Value(currentSeed));

		try {
			var instrs = block.Instructions;
			int end = instrs.Count;
			// Skip trailing branches (they only affect control flow, not locals)
			while (end > 0 && (instrs[end - 1].IsBr() || instrs[end - 1].IsConditionalBranch()))
				end--;
			emu.Emulate(instrs, 0, end);
		}
		catch {
			return null;
		}

		var sv = emu.GetLocal(_dispatch.StateVar);
		if (sv is Int32Value svIv && svIv.AllBitsValid())
			return svIv.Value;
		return null;
	}

	/// <summary>
	///     Finds a simple path from start to target via BFS. Returns null if
	///     no path exists within 30 blocks or if the path is ambiguous.
	/// </summary>
	List<Block> FindSimplePath(Block start, Block target) {
		if (start == target)
			return new List<Block> { start };

		const int maxBlocks = 100;
		var parent = new Dictionary<Block, Block>();
		var queue = new Queue<Block>();
		queue.Enqueue(start);
		parent[start] = null;
		int visited = 0;

		while (queue.Count > 0 && visited < maxBlocks) {
			var block = queue.Dequeue();
			visited++;

			foreach (var succ in block.GetTargets()) {
				if (IsDispatchBlock(succ))
					continue;
				if (parent.ContainsKey(succ))
					continue;

				parent[succ] = block;

				if (succ == target) {
					// Reconstruct path
					var path = new List<Block>();
					var cur = target;
					while (cur is not null) {
						path.Add(cur);
						cur = parent[cur];
					}
					path.Reverse();
					return path;
				}

				queue.Enqueue(succ);
			}
		}

		return null;
	}

	/// <summary>
	///     Derives the next-case seed and case index by tracing stateVar from a case
	///     target to a dispatch predecessor. Tries two approaches:
	///     1. Block-by-block stateVar tracing (resilient to calls/field loads) + isolated
	///        dispatch emulation
	///     2. Full path emulation (handles stack-based patterns where xor constants come
	///        from previous blocks on the stack)
	/// </summary>
	(int seed, int caseIdx)? TryEmulateForSeed(Block caseTarget, Block dispatchPred, int caseSeed, int srcCaseIdx = -1) {
		if (_dispatch.StateVar is null)
			return null;

		// Approach 1: Block-by-block stateVar tracing + isolated dispatch emulation.
		// This handles cases where intermediate blocks have calls/field loads that
		// produce unknowns on the stack, as long as the stateVar update only depends
		// on stateVar and constants.
		int? stateAtEntry = TraceStateVarForward(caseTarget, dispatchPred, caseSeed);
		if (stateAtEntry is not null) {
			var result = EmulateDispatchPredAndDispatch(dispatchPred, stateAtEntry.Value);
			if (result is not null)
				return result;
		}

		// Approach 2: Full path emulation (handles stack-based xor patterns where
		// the xor constant is pushed by an earlier block and consumed by the dispatch
		// predecessor via the stack).
		return FullPathEmulateForSeed(caseTarget, dispatchPred, caseSeed, srcCaseIdx);
	}

	/// <summary>
	///     Emulates a dispatch predecessor block and the dispatch blocks with the
	///     given stateVar seed. Returns the derived seed and case index.
	/// </summary>
	(int seed, int caseIdx)? EmulateDispatchPredAndDispatch(Block dispatchPred, int stateVarSeed) {
		var emu = new InstructionEmulator();
		emu.Initialize(_method, false);
		emu.SetLocal(_dispatch.StateVar, new Int32Value(stateVarSeed));

		try {
			var instrs = dispatchPred.Instructions;
			int end = instrs.Count;
			if (end > 0 && instrs[end - 1].IsBr())
				end--;
			emu.Emulate(instrs, 0, end);
			EmulateDispatchBlocks(emu, dispatchPred, out _);
		}
		catch {
			return null;
		}

		if (emu.StackSize() < 1)
			return null;

		var tos = emu.Pop();
		if (tos is not Int32Value iv || !iv.AllBitsValid())
			return null;

		int caseIdx = iv.Value;
		if (caseIdx < 0 || caseIdx >= _dispatch.CaseTargets.Count)
			return null;

		var sv = emu.GetLocal(_dispatch.StateVar);
		if (sv is not Int32Value svIv || !svIv.AllBitsValid())
			return null;

		return (svIv.Value, caseIdx);
	}

	/// <summary>
	///     Full-path emulation from case target to dispatch predecessor. This handles
	///     stack-based patterns (e.g., xor constant pushed at case entry, consumed at
	///     dispatch predecessor) but fails when intermediate blocks have calls that
	///     corrupt the stack with unknowns.
	/// </summary>
	(int seed, int caseIdx)? FullPathEmulateForSeed(Block caseTarget, Block dispatchPred, int caseSeed, int srcCaseIdx = -1) {
		var path = FindSimplePath(caseTarget, dispatchPred);
		if (path is null)
			return null;

		var emu = new InstructionEmulator();
		emu.Initialize(_method, false);
		emu.SetLocal(_dispatch.StateVar, new Int32Value(caseSeed));

		try {
			foreach (var block in path) {
				var instrs = block.Instructions;
				int end = instrs.Count;
				if (end > 0 && instrs[end - 1].IsBr())
					end--;
				emu.Emulate(instrs, 0, end);
			}
			EmulateDispatchBlocks(emu, dispatchPred, out _);
		}
		catch {
			return null;
		}

		if (emu.StackSize() < 1)
			return null;

		var tos = emu.Pop();
		if (tos is not Int32Value iv || !iv.AllBitsValid())
			return null;

		int caseIdx = iv.Value;
		if (caseIdx < 0 || caseIdx >= _dispatch.CaseTargets.Count)
			return null;

		var sv = emu.GetLocal(_dispatch.StateVar);
		if (sv is not Int32Value svIv || !svIv.AllBitsValid())
			return null;

		return (svIv.Value, caseIdx);
	}

	// --- Phase 5: Algebraic seed extraction ---

	/// <summary>
	///     Discovers new seeds by algebraically computing next-case state values
	///     from affine update patterns in blocks that return to the dispatch.
	/// </summary>
	int RunAlgebraicSeedExtraction(Dictionary<int, int> caseStateVar, HashSet<int> allSeeds) {
		if (_dispatch.StateVar is null)
			return 0;

		var switchConstants = ExtractSwitchConstants();
		if (switchConstants is null)
			return 0;

		var (xorKey, modulus) = switchConstants.Value;
		if ((uint)modulus != (uint)_dispatch.CaseTargets.Count)
			return 0;

		var dispatchPreds = new HashSet<Block>(GetDispatchPredecessors());

		// Collect all derived seeds: nextCase → set of nextSeed values
		var derivedSeeds = new Dictionary<int, HashSet<int>>();

		for (int caseIdx = 0; caseIdx < _dispatch.CaseTargets.Count; caseIdx++) {
			if (!caseStateVar.TryGetValue(caseIdx, out int seed))
				continue;

			var caseTarget = _dispatch.CaseTargets[caseIdx];

			// BFS forward from case target, bounded by 50 visited blocks
			var queue = new Queue<Block>();
			var visited = new HashSet<Block>();
			queue.Enqueue(caseTarget);
			visited.Add(caseTarget);
			int visitCount = 0;

			while (queue.Count > 0 && visitCount < 50) {
				var block = queue.Dequeue();
				visitCount++;

				if (IsDispatchBlock(block))
					continue;

				// Check if block is a dispatch predecessor
				bool isDispatchPred = dispatchPreds.Contains(block);
				if (!isDispatchPred) {
					if (IsDispatchBlock(block.FallThrough))
						isDispatchPred = true;
					else if (block.LastInstr.IsBr() && block.Targets is { Count: 1 } && IsDispatchBlock(block.Targets[0]))
						isDispatchPred = true;
				}

				if (isDispatchPred) {
					int? nextSeed = null;
					int? nextCase = null;

					// Try algebraic extraction first (fast path)
					var affine = ExtractAffineUpdate(block, _dispatch.StateVar, _blocks.Locals);
					if (affine is not null) {
						var (mulConst, xorConst) = affine.Value;
						uint nextSeedU = unchecked(((uint)seed * (uint)mulConst) ^ (uint)xorConst ^ (uint)xorKey);
						nextSeed = (int)nextSeedU;
						nextCase = (int)(nextSeedU % (uint)modulus);
					}

					// Fallback: emulation-based seed derivation
					// Handles non-standard patterns (stack-based xor, split constants)
					if (nextSeed is null) {
						var emuResult = TryEmulateForSeed(caseTarget, block, seed, caseIdx);
						if (emuResult is not null) {
							nextSeed = emuResult.Value.seed;
							nextCase = emuResult.Value.caseIdx;
						}
					}

					if (nextSeed is not null && nextCase is not null &&
						nextCase.Value >= 0 && nextCase.Value < _dispatch.CaseTargets.Count) {
						if (!derivedSeeds.TryGetValue(nextCase.Value, out var seedSet)) {
							seedSet = [];
							derivedSeeds[nextCase.Value] = seedSet;
						}
						seedSet.Add(nextSeed.Value);
					}
				}

				// Enqueue successors
				foreach (var succ in block.GetTargets()) {
					if (visited.Add(succ))
						queue.Enqueue(succ);
				}
			}
		}

		// Merge: only accept unambiguous derived seeds
		int newSeedCount = 0;
		foreach (var kv in derivedSeeds) {
			int nextCase = kv.Key;
			var seedSet = kv.Value;

			if (seedSet.Count != 1)
				continue; // ambiguous — multiple different seeds for same case

			int nextSeed = seedSet.FirstOrDefault();

			if (caseStateVar.TryGetValue(nextCase, out int _))
				continue;

			caseStateVar[nextCase] = nextSeed;
			allSeeds.Add(nextSeed);
			newSeedCount++;
		}

		return newSeedCount;
	}

	/// <summary>
	///     O(1) algebraic check: does this seed route to the expected case index?
	///     The seed is already the post-XOR dispatch value (V_7 = incoming_state ^ K),
	///     so case_index = seed % M with no additional XOR.
	/// </summary>
	bool VerifySeedRoutesToCase(int seed, int expectedCaseIndex) {
		int M = _dispatch.CaseTargets.Count;
		if (M < 2)
			return true;
		return (int)((uint)seed % (uint)M) == expectedCaseIndex;
	}

	// --- Helper methods ---

	static int FindSwitchIndex(IList<Instr> instrs) {
		for (int i = instrs.Count - 1; i >= 0; i--) {
			if (instrs[i].OpCode.Code == Code.Switch)
				return i;
		}
		return -1;
	}

	static int FindRemUnIndex(IList<Instr> instrs) {
		int switchIdx = FindSwitchIndex(instrs);
		if (switchIdx < 0)
			return -1;
		for (int i = switchIdx - 1; i >= 0; i--) {
			if (instrs[i].OpCode.Code == Code.Rem_Un)
				return i;
		}
		return -1;
	}

	/// <summary>
	///     Extracts the XOR key (K) and modulus (M) from the dispatch blocks' instruction
	///     pattern: ldloc stateVar; ldc.i4 K; xor; dup; stloc; ldc.i4 M; rem.un; switch.
	///     Checks both the switch block and header block (if present).
	/// </summary>
	(int xorKey, int modulus)? ExtractSwitchConstants() {
		var result = ExtractSwitchConstantsFromBlock(_dispatch.SwitchBlock);
		if (result is not null)
			return result;
		if (_dispatch.HeaderBlock is not null)
			return ExtractSwitchConstantsFromBlock(_dispatch.HeaderBlock);
		return null;
	}

	static (int xorKey, int modulus)? ExtractSwitchConstantsFromBlock(Block block) {
		var instrs = block.Instructions;

		// Find rem.un in this block
		int remIdx = -1;
		for (int i = instrs.Count - 1; i >= 0; i--) {
			if (instrs[i].OpCode.Code == Code.Rem_Un) {
				remIdx = i;
				break;
			}
		}
		if (remIdx < 0)
			return null;

		// Verify no other rem* opcodes after remIdx
		for (int i = remIdx + 1; i < instrs.Count; i++) {
			var code = instrs[i].OpCode.Code;
			if (code is Code.Rem or Code.Rem_Un)
				return null;
		}

		// Find ldc.i4 M immediately before rem.un (allowing only nop/dup/stloc between)
		int mIdx = -1;
		for (int i = remIdx - 1; i >= 0; i--) {
			if (instrs[i].IsLdcI4()) {
				mIdx = i;
				break;
			}
			var code = instrs[i].OpCode.Code;
			if (code != Code.Nop && code != Code.Dup && !instrs[i].IsStloc())
				break;
		}
		if (mIdx < 0)
			return null;

		int modulus = instrs[mIdx].GetLdcI4Value();

		// Find xor before mIdx (skip dup/stloc/nop)
		int xorIdx = -1;
		for (int i = mIdx - 1; i >= 0; i--) {
			if (instrs[i].OpCode.Code == Code.Xor) {
				xorIdx = i;
				break;
			}
			var code = instrs[i].OpCode.Code;
			if (code != Code.Nop && code != Code.Dup && !instrs[i].IsStloc())
				break;
		}
		if (xorIdx < 0)
			return null;

		// Find ldc.i4 K immediately before xor
		if (xorIdx < 1)
			return null;
		if (!instrs[xorIdx - 1].IsLdcI4())
			return null;

		return (instrs[xorIdx - 1].GetLdcI4Value(), modulus);
	}

	/// <summary>
	///     Extracts affine state-update constants from a block by finding the pattern:
	///     ldloc stateVar; ldc.i4 A; mul; ldc.i4 B; xor
	///     closest to the block end. Also handles one-step aliases.
	/// </summary>
	static (int mulConst, int xorConst)? ExtractAffineUpdate(Block block, Local stateVar, IList<Local> locals) {
		var instrs = block.Instructions;
		int endIdx = instrs.Count - 1;

		// Skip final br
		if (endIdx >= 0 && instrs[endIdx].IsBr())
			endIdx--;

		// Scan backward for: ldloc stateVar; ldc.i4 A; mul; ldc.i4 B; xor
		// Take the match closest to the block end
		for (int i = endIdx; i >= 4; i--) {
			if (instrs[i].OpCode.Code != Code.Xor)
				continue;
			if (!instrs[i - 1].IsLdcI4())
				continue;
			if (instrs[i - 2].OpCode.Code != Code.Mul)
				continue;
			if (!instrs[i - 3].IsLdcI4())
				continue;
			if (!instrs[i - 4].IsLdloc())
				continue;

			var loadedLocal = Instr.GetLocalVar(locals, instrs[i - 4]);
			bool isStateVar = loadedLocal == stateVar;

			// Alias tracking: check if loadedLocal is a one-step alias of stateVar
			if (!isStateVar && loadedLocal is not null) {
				for (int j = i - 5; j >= 1; j--) {
					if (instrs[j].IsStloc() && Instr.GetLocalVar(locals, instrs[j]) == loadedLocal &&
						instrs[j - 1].IsLdloc() && Instr.GetLocalVar(locals, instrs[j - 1]) == stateVar) {
						isStateVar = true;
						break;
					}
				}
			}

			if (!isStateVar)
				continue;

			// Verify: between match end (i) and block end (endIdx), only harmless ops
			bool clean = true;
			for (int j = i + 1; j <= endIdx; j++) {
				var code = instrs[j].OpCode.Code;
				if (code == Code.Nop || code == Code.Dup || instrs[j].IsStloc() ||
					code == Code.Br || code == Code.Br_S)
					continue;
				clean = false;
				break;
			}

			if (clean)
				return (instrs[i - 3].GetLdcI4Value(), instrs[i - 1].GetLdcI4Value());
		}

		return null;
	}

	/// <summary>
	///     Determines the case index from a pre-rem.un dividend with partial bit validity.
	///     For power-of-two moduli, only the low bits matter. For non-power-of-two moduli,
	///     enumerates unknown bit combinations to check if the remainder is unique.
	/// </summary>
	int? TryPartialCaseIndex(Int32Value preRemDividend) {
		if (preRemDividend is null)
			return null;
		int M = _dispatch.CaseTargets.Count;
		if (M < 2)
			return null;
		uint mu = (uint)M;

		// Power-of-two fast path: only low bits matter
		if ((mu & (mu - 1)) == 0) {
			uint neededMask = mu - 1;
			if ((preRemDividend.ValidMask & neededMask) == neededMask)
				return (int)((uint)preRemDividend.Value & neededMask);
			return null;
		}

		// Non-power-of-two: enumerate unknown bit combinations
		if (preRemDividend.AllBitsValid())
			return (int)((uint)preRemDividend.Value % mu);

		uint unknownMask = ~preRemDividend.ValidMask;
		int unknownCount = PopCount(unknownMask);
		if (unknownCount == 0)
			return (int)((uint)preRemDividend.Value % mu);
		if (unknownCount > 16)
			return null;
		if (unknownCount > 14 && M > 128)
			return null;

		// Cheap early-out: if flipping just the lowest unknown bit changes the
		// remainder, the full enumeration is guaranteed to be ambiguous
		int base0 = ExpandUnknownBits(preRemDividend.Value, unknownMask, 0);
		int base1 = ExpandUnknownBits(preRemDividend.Value, unknownMask, 1);
		int rem0 = (int)((uint)base0 % mu);
		int rem1 = (int)((uint)base1 % mu);
		if (rem0 != rem1)
			return null;

		int? result = rem0;
		for (uint combo = 2; combo < (1u << unknownCount); combo++) {
			int candidate = ExpandUnknownBits(preRemDividend.Value, unknownMask, combo);
			int rem = (int)((uint)candidate % mu);
			if (rem != result.Value)
				return null; // ambiguous
		}
		return result;
	}

	static int ExpandUnknownBits(int baseValue, uint unknownMask, uint combo) {
		uint v = (uint)baseValue;
		uint m = unknownMask;
		uint bit = 1;
		while (m != 0) {
			uint lsb = m & (~m + 1u); // extract lowest set bit
			m ^= lsb;
			if ((combo & bit) != 0)
				v |= lsb;
			else
				v &= ~lsb;
			bit <<= 1;
		}
		return (int)v;
	}

	static int PopCount(uint value) {
		value -= (value >> 1) & 0x55555555u;
		value = (value & 0x33333333u) + ((value >> 2) & 0x33333333u);
		return (int)(((value + (value >> 4)) & 0x0F0F0F0Fu) * 0x01010101u >> 24);
	}
}
