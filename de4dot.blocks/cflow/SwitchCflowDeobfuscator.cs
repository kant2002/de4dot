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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using dnlib.DotNet.Emit;

namespace de4dot.blocks.cflow {
	class SwitchCflowDeobfuscator : BlockDeobfuscator, ISwitchDispatchResolver {
		InstructionEmulator instructionEmulator = new InstructionEmulator();

		public bool SuppressDispatchResolution { get; set; }

		/// <summary>
		///     One pending switch rewrite. Produced by the Plan* methods without touching the graph, so a
		///     whole set can be validated together before any of it is applied — see
		///     <see cref="WouldOrphanMethodExit"/> for why validating them one at a time is not enough.
		/// </summary>
		readonly struct SwitchRewrite {
			public readonly Block Source;
			public readonly Block Target;
			/// <summary>Non-null only for Bcc sources: the switch block whose incoming edges get replaced.</summary>
			public readonly Block? SwitchBlock;
			public readonly bool AddPop;

			SwitchRewrite(Block source, Block target, Block? switchBlock, bool addPop) {
				Source = source;
				Target = target;
				SwitchBlock = switchBlock;
				AddPop = addPop;
			}

			public static SwitchRewrite Branch(Block source, Block target) => new SwitchRewrite(source, target, null, false);
			public static SwitchRewrite BranchWithPop(Block source, Block target) => new SwitchRewrite(source, target, null, true);
			public static SwitchRewrite Bcc(Block source, Block target, Block switchBlock) => new SwitchRewrite(source, target, switchBlock, false);

			/// <summary>Successors this rewrite would give <see cref="Source"/>.</summary>
			public List<Block> ResultingSuccessors() {
				if (SwitchBlock == null)
					return new List<Block> { Target };
				return SuccessorsWithBlockReplaced(Source, SwitchBlock, Target);
			}
		}

		/// <summary>
		///     Validates a whole plan and applies it only if it keeps a ret/throw reachable. Returns
		///     whether anything was applied.
		/// </summary>
		bool ApplyPlan(List<SwitchRewrite> plan) {
			Debug.Assert(allBlocks != null);
			if (plan.Count == 0)
				return false;

			// One source can appear in several sub-plans (its own redirect plus a recursive fallback).
			// Keep the first, so the simulated graph matches what Apply actually produces.
			var seen = new HashSet<Block>();
			var deduped = new List<SwitchRewrite>(plan.Count);
			foreach (var r in plan) {
				if (seen.Add(r.Source))
					deduped.Add(r);
			}

			var overrides = new Dictionary<Block, List<Block>>();
			foreach (var r in deduped)
				overrides[r.Source] = r.ResultingSuccessors();
			if (WouldOrphanMethodExit(allBlocks, overrides))
				return false;

			bool modified = false;
			foreach (var r in deduped) {
				if (r.SwitchBlock == null) {
					r.Source.ReplaceLastNonBranchWithBranch(0, r.Target);
					if (r.AddPop)
						r.Source.Add(new Instr(OpCodes.Pop.ToInstruction()));
					modified = true;
				}
				else {
					Debug.Assert(r.Source.Targets != null);
					if (r.Source.Targets[0] == r.SwitchBlock) {
						r.Source.SetNewTarget(0, r.Target);
						modified = true;
					}
					if (r.Source.FallThrough == r.SwitchBlock) {
						r.Source.SetNewFallThrough(r.Target);
						modified = true;
					}
				}
			}
			return modified;
		}

		protected override bool Deobfuscate(Block switchBlock) {
			if (SuppressDispatchResolution)
				return false;
			if (switchBlock.LastInstr.OpCode.Code != Code.Switch)
				return false;

			if (IsSwitchTopOfStack(switchBlock) && DeobfuscateTOS(switchBlock))
				return true;

			if (IsLdlocBranch(switchBlock, true) && DeobfuscateLdloc(switchBlock))
				return true;

			if (IsStLdlocBranch(switchBlock, true) && DeobfuscateStLdloc(switchBlock))
				return true;

			if (IsSwitchType1(switchBlock) && DeobfuscateType1(switchBlock))
				return true;

			if (IsSwitchType2(switchBlock) && DeobfuscateType2(switchBlock))
				return true;

			if (switchBlock.FirstInstr.IsLdloc() && FixSwitchBranch(switchBlock))
				return true;

			return false;
		}

		static bool IsSwitchTopOfStack(Block switchBlock) => switchBlock.Instructions.Count == 1;

		static bool IsLdlocBranch(Block switchBlock, bool isSwitch) {
			int numInstrs = 1 + (isSwitch ? 1 : 0);
			return switchBlock.Instructions.Count == numInstrs && switchBlock.Instructions[0].IsLdloc();
		}

		static bool IsSwitchType1(Block switchBlock) => switchBlock.FirstInstr.IsLdloc();

		bool IsSwitchType2(Block switchBlock) {
			Debug.Assert(blocks != null);
			Local? local = null;
			foreach (var instr in switchBlock.Instructions) {
				if (!instr.IsLdloc())
					continue;
				local = Instr.GetLocalVar(blocks.Locals, instr);
				break;
			}
			if (local == null)
				return false;

			foreach (var source in switchBlock.Sources) {
				var instrs = source.Instructions;
				for (int i = 1; i < instrs.Count; i++) {
					var ldci4 = instrs[i - 1];
					if (!ldci4.IsLdcI4())
						continue;
					var stloc = instrs[i];
					if (!stloc.IsStloc())
						continue;
					if (Instr.GetLocalVar(blocks.Locals, stloc) != local)
						continue;

					return true;
				}
			}

			return false;
		}

		bool IsStLdlocBranch(Block switchBlock, bool isSwitch) {
			Debug.Assert(blocks != null);
			int numInstrs = 2 + (isSwitch ? 1 : 0);
			return switchBlock.Instructions.Count == numInstrs &&
				switchBlock.Instructions[0].IsStloc() &&
				switchBlock.Instructions[1].IsLdloc() &&
				Instr.GetLocalVar(blocks.Locals, switchBlock.Instructions[0]) == Instr.GetLocalVar(blocks.Locals, switchBlock.Instructions[1]);
		}

		bool DeobfuscateTOS(Block switchBlock) {
			if (switchBlock.Targets == null)
				return false;
			var targets = new List<Block>(switchBlock.Targets);

			Debug.Assert(switchBlock.FallThrough != null);
			var plan = new List<SwitchRewrite>();
			PlanTOS(targets, switchBlock.FallThrough, switchBlock, plan);
			return ApplyPlan(plan);
		}

		bool DeobfuscateLdloc(Block switchBlock) {
			Debug.Assert(blocks != null);
			var switchVariable = Instr.GetLocalVar(blocks.Locals, switchBlock.Instructions[0]);
			if (switchVariable == null)
				return false;
			if (switchBlock.Targets == null)
				return false;
			var targets = new List<Block>(switchBlock.Targets);

			Debug.Assert(switchBlock.FallThrough != null);
			var plan = new List<SwitchRewrite>();
			PlanLdloc(targets, switchBlock.FallThrough, switchBlock, switchVariable, plan);
			return ApplyPlan(plan);
		}

		bool DeobfuscateStLdloc(Block switchBlock) {
			Debug.Assert(blocks != null);
			var switchVariable = Instr.GetLocalVar(blocks.Locals, switchBlock.Instructions[0]);
			if (switchVariable == null)
				return false;
			if (switchBlock.Targets == null)
				return false;
			var targets = new List<Block>(switchBlock.Targets);

			Debug.Assert(switchBlock.FallThrough != null);
			var plan = new List<SwitchRewrite>();
			PlanStLdloc(targets, switchBlock.FallThrough, switchBlock, plan);
			return ApplyPlan(plan);
		}

		// Switch deobfuscation when block uses stloc N, ldloc N to load switch constant
		//	blk1:
		//		ldc.i4 X
		//		br swblk
		//	swblk:
		//		stloc N
		//		ldloc N
		//		switch (......)
		// Plan phase: works out the rewrites without touching the graph, so several plans can be
		// unioned and validated together before anything is applied.
		void PlanStLdloc(IList<Block> switchTargets, Block switchFallThrough, Block block, List<SwitchRewrite> plan) {
			Debug.Assert(blocks != null);
			Debug.Assert(allBlocks != null);
			foreach (var source in new List<Block>(block.Sources)) {
				if (!IsBranchBlock(source))
					continue;
				instructionEmulator.Initialize(blocks, allBlocks[0] == source);
				instructionEmulator.Emulate(source.Instructions);

				var target = GetSwitchTarget(switchTargets, switchFallThrough, instructionEmulator.Pop());
				if (target == null)
					continue;
				plan.Add(SwitchRewrite.BranchWithPop(source, target));
			}
		}

		// Switch deobfuscation when block uses ldloc N to load switch constant
		//	blk1:
		//		ldc.i4 X
		//		stloc N
		//		br swblk / bcc swblk
		//	swblk:
		//		ldloc N
		//		switch (......)
		void PlanLdloc(IList<Block> switchTargets, Block switchFallThrough, Block block, Local switchVariable, List<SwitchRewrite> plan) {
			Debug.Assert(blocks != null);
			Debug.Assert(allBlocks != null);
			foreach (var source in new List<Block>(block.Sources)) {
				bool isBranch = IsBranchBlock(source);
				if (!isBranch && !IsBccBlock(source))
					continue;

				instructionEmulator.Initialize(blocks, allBlocks[0] == source);
				instructionEmulator.Emulate(source.Instructions);

				var target = GetSwitchTarget(switchTargets, switchFallThrough, instructionEmulator.GetLocal(switchVariable));
				if (target == null)
					continue;

				plan.Add(isBranch
					? SwitchRewrite.Branch(source, target)
					: SwitchRewrite.Bcc(source, target, block));
					}
				}

		/// <summary>Successors of <paramref name="source"/> with every edge to <paramref name="oldTarget"/> pointed at <paramref name="newTarget"/>.</summary>
		static List<Block> SuccessorsWithBlockReplaced(Block source, Block oldTarget, Block newTarget) {
			var list = new List<Block>();
			if (source.FallThrough != null)
				list.Add(source.FallThrough == oldTarget ? newTarget : source.FallThrough);
			if (source.Targets != null) {
				foreach (var t in source.Targets) {
					if (t != null)
						list.Add(t == oldTarget ? newTarget : t);
			}
			}
			return list;
		}

		// Switch deobfuscation when block has switch contant on TOS:
		//	blk1:
		//		ldc.i4 X
		//		br swblk
		//	swblk:
		//		switch (......)
		/// <summary>
		///     Would redirecting each <c>source -> target</c> leave the method with no reachable
		///     ret/throw?
		///
		///     Resolving a switch redirects every source of the switch block straight to its own
		///     target. Once the last source is redirected the switch block is unreachable, and so is
		///     any switch target that no source resolved to — which can be the one holding the
		///     method's only exit. Nothing notices at that point because the blocks still exist; the
		///     dead-block cleanup on the next iteration removes them, and the method is left looping
		///     forever. That is perfectly verifiable IL, so ilverify cannot catch it either.
		///
		///     Simulated on a copy of the successor map, so this is read-only. When it returns true
		///     the caller leaves the switch alone, which is always better than silently deleting the
		///     path to the method's exit.
		/// </summary>
		static bool WouldOrphanMethodExit(List<Block> allBlocks, Dictionary<Block, List<Block>> overrides) {
			if (allBlocks.Count == 0 || overrides.Count == 0)
				return false;

			var seen = new HashSet<Block>();
			var stack = new Stack<Block>();
			var handlerEntries = new List<Block>();
			stack.Push(allBlocks[0]);
			while (stack.Count > 0) {
				var block = stack.Pop();
				if (!seen.Add(block))
					continue;
				foreach (var instr in block.Instructions) {
					var code = instr.OpCode.Code;
					if (code == Code.Ret || code == Code.Throw || code == Code.Rethrow)
						return false;
				}

				if (overrides.TryGetValue(block, out var rewritten)) {
					foreach (var target in rewritten)
						stack.Push(target);
				}
				else {
					if (block.FallThrough != null)
						stack.Push(block.FallThrough);
					if (block.Targets != null) {
						foreach (var target in block.Targets) {
							if (target != null)
								stack.Push(target);
						}
					}
				}

				// Reaching a protected block makes its handlers reachable. Handler blocks are not in
				// allBlocks and nothing branches into them, so without this a `throw` that lives only
				// in a catch is invisible here and the rewrite gets refused on a method that exits
				// perfectly well.
				handlerEntries.Clear();
				ScopeBlock.AddProtectingHandlerEntryBlocks(block, handlerEntries);
				foreach (var entry in handlerEntries)
					stack.Push(entry);
			}
			return true;
		}

		void PlanTOS(IList<Block> switchTargets, Block switchFallThrough, Block block, List<SwitchRewrite> plan) {
			Debug.Assert(blocks != null);
			Debug.Assert(allBlocks != null);
			foreach (var source in new List<Block>(block.Sources)) {
				if (!IsBranchBlock(source))
					continue;
				instructionEmulator.Initialize(blocks, allBlocks[0] == source);
				instructionEmulator.Emulate(source.Instructions);

				var target = GetSwitchTarget(switchTargets, switchFallThrough, instructionEmulator.Pop());
				if (target == null) {
					// The constant is not on this source's stack; it may be one level further up, in
					// which case that rewrite belongs to the SAME plan — validating it separately is
					// what used to let the two of them jointly orphan the method's exit.
					PlanTos_Ldloc(switchTargets, switchFallThrough, source, plan);
				}
				else
					plan.Add(SwitchRewrite.BranchWithPop(source, target));
				}
		}

		//		ldloc N
		//		br swblk
		// or
		//		stloc N
		//		ldloc N
		//		br swblk
		void PlanTos_Ldloc(IList<Block> switchTargets, Block switchFallThrough, Block block, List<SwitchRewrite> plan) {
			Debug.Assert(blocks != null);
			if (IsLdlocBranch(block, false)) {
				var switchVariable = Instr.GetLocalVar(blocks.Locals, block.Instructions[0]);
				if (switchVariable == null)
					return;
				PlanLdloc(switchTargets, switchFallThrough, block, switchVariable, plan);
			}
			else if (IsStLdlocBranch(block, false))
				PlanStLdloc(switchTargets, switchFallThrough, block, plan);
		}

		static bool IsBranchBlock(Block block) {
			if (block.Targets != null)
				return false;
			if (block.FallThrough == null)
				return false;
			switch (block.LastInstr.OpCode.Code) {
			case Code.Switch:
			case Code.Leave:
			case Code.Leave_S:
				return false;
			default:
				return true;
			}
		}

		static bool IsBccBlock(Block block) {
			if (block.Targets == null || block.Targets.Count != 1)
				return false;
			if (block.FallThrough == null)
				return false;
			switch (block.LastInstr.OpCode.Code) {
			case Code.Beq:
			case Code.Beq_S:
			case Code.Bge:
			case Code.Bge_S:
			case Code.Bge_Un:
			case Code.Bge_Un_S:
			case Code.Bgt:
			case Code.Bgt_S:
			case Code.Bgt_Un:
			case Code.Bgt_Un_S:
			case Code.Ble:
			case Code.Ble_S:
			case Code.Ble_Un:
			case Code.Ble_Un_S:
			case Code.Blt:
			case Code.Blt_S:
			case Code.Blt_Un:
			case Code.Blt_Un_S:
			case Code.Bne_Un:
			case Code.Bne_Un_S:
			case Code.Brfalse:
			case Code.Brfalse_S:
			case Code.Brtrue:
			case Code.Brtrue_S:
				return true;
			default:
				return false;
			}
		}

		bool DeobfuscateType1(Block switchBlock) {
			if (!EmulateGetTarget(switchBlock, out var target) || target != null)
				return false;

			bool modified = false;

			foreach (var source in new List<Block>(switchBlock.Sources)) {
				if (!source.CanAppend(switchBlock))
					continue;
				if (!WillHaveKnownTarget(switchBlock, source))
					continue;

				source.Append(switchBlock);
				modified = true;
			}

			return modified;
		}

		bool DeobfuscateType2(Block switchBlock) {
			bool modified = false;

			var bccSources = new List<Block>();
			foreach (var source in new List<Block>(switchBlock.Sources)) {
				if (source.LastInstr.IsConditionalBranch()) {
					bccSources.Add(source);
					continue;
				}
				if (!source.CanAppend(switchBlock))
					continue;
				if (!WillHaveKnownTarget(switchBlock, source))
					continue;

				source.Append(switchBlock);
				modified = true;
			}

			foreach (var bccSource in bccSources) {
				if (!WillHaveKnownTarget(switchBlock, bccSource))
					continue;
				var consts = GetBccLocalConstants(bccSource);
				if (consts.Count == 0)
					continue;
				Debug.Assert(bccSource.FallThrough != null);
				Debug.Assert(bccSource.Targets != null);
				var newFallThrough = CreateBlock(consts, bccSource.FallThrough);
				var newTarget = CreateBlock(consts, bccSource.Targets[0]);
				var oldFallThrough = bccSource.FallThrough;
				var oldTarget = bccSource.Targets[0];
				bccSource.SetNewFallThrough(newFallThrough);
				bccSource.SetNewTarget(0, newTarget);
				newFallThrough.SetNewFallThrough(oldFallThrough);
				newTarget.SetNewFallThrough(oldTarget);
				modified = true;
			}

			return modified;
		}

		static Block CreateBlock(Dictionary<Local, int> consts, Block fallThrough) {
			var block = new Block();
			foreach (var kv in consts) {
				block.Instructions.Add(new Instr(Instruction.CreateLdcI4(kv.Value)));
				block.Instructions.Add(new Instr(OpCodes.Stloc.ToInstruction(kv.Key)));
			}
			Debug.Assert(fallThrough.Parent != null);
			fallThrough.Parent.Add(block);
			return block;
		}

		Dictionary<Local, int> GetBccLocalConstants(Block block) {
			Debug.Assert(blocks != null);
			var dict = new Dictionary<Local, int>();
			var instrs = block.Instructions;
			for (int i = 0; i < instrs.Count; i++) {
				var instr = instrs[i];
				if (instr.IsStloc()) {
					var local = Instr.GetLocalVar(blocks.Locals, instr);
					if (local == null)
						continue;
					var ldci4 = i == 0 ? null : instrs[i - 1];
					if (ldci4 == null || !ldci4.IsLdcI4())
						dict.Remove(local);
					else
						dict[local] = ldci4.GetLdcI4Value();
				}
				else if (instr.IsLdloc()) {
					var local = Instr.GetLocalVar(blocks.Locals, instr);
					if (local != null)
						dict.Remove(local);
				}
				else if (instr.OpCode.Code == Code.Ldloca || instr.OpCode.Code == Code.Ldloca_S) {
					if (instr.Operand is Local local)
						dict.Remove(local);
				}
			}
			return dict;
		}

		bool EmulateGetTarget(Block switchBlock, out Block? target) {
			Debug.Assert(blocks != null);
			Debug.Assert(allBlocks != null);
			instructionEmulator.Initialize(blocks, allBlocks[0] == switchBlock);
			try {
				instructionEmulator.Emulate(switchBlock.Instructions, 0, switchBlock.Instructions.Count - 1);
			}
			catch (NullReferenceException) {
				// Here if eg. invalid metadata token in a call instruction (operand is null)
				target = null;
				return false;
			}
			target = GetTarget(switchBlock);
			return true;
		}

		bool WillHaveKnownTarget(Block switchBlock, Block source) {
			Debug.Assert(blocks != null);
			Debug.Assert(allBlocks != null);
			instructionEmulator.Initialize(blocks, allBlocks[0] == source);
			try {
				instructionEmulator.Emulate(source.Instructions);
				instructionEmulator.Emulate(switchBlock.Instructions, 0, switchBlock.Instructions.Count - 1);
			}
			catch (NullReferenceException) {
				// Here if eg. invalid metadata token in a call instruction (operand is null)
				return false;
			}
			return GetTarget(switchBlock) != null;
		}

		Block? GetTarget(Block switchBlock) {
			var val1 = instructionEmulator.Pop();
			if (!val1.IsInt32())
				return null;
			Debug.Assert(switchBlock.FallThrough != null);
			return CflowUtils.GetSwitchTarget(switchBlock.Targets, switchBlock.FallThrough, (Int32Value)val1);
		}

		static Block? GetSwitchTarget(IList<Block> targets, Block fallThrough, Value value) {
			if (!value.IsInt32())
				return null;
			return CflowUtils.GetSwitchTarget(targets, fallThrough, (Int32Value)value);
		}

		static bool FixSwitchBranch(Block switchBlock) {
			// Code:
			//	blk1:
			//		ldc.i4 XXX
			//		br common
			//	blk2:
			//		ldc.i4 YYY
			//		br common
			//	common:
			//		stloc X
			//		br swblk
			//	swblk:
			//		ldloc X
			//		switch
			// Inline common into blk1 and blk2.

			bool modified = false;

			foreach (var commonSource in new List<Block>(switchBlock.Sources)) {
				if (commonSource.Instructions.Count != 1)
					continue;
				if (!commonSource.FirstInstr.IsStloc())
					continue;
				foreach (var blk in new List<Block>(commonSource.Sources)) {
					if (blk.CanAppend(commonSource)) {
						blk.Append(commonSource);
						modified = true;
					}
				}
			}

			return modified;
		}
	}
}
