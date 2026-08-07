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
using System.Diagnostics;
using dnlib.DotNet.Emit;

namespace de4dot.blocks.cflow {
	class BlockCflowDeobfuscator : BlockDeobfuscator, IBranchHandler, ISwitchDispatchResolver {
		Block? block;
		InstructionEmulator instructionEmulator;
		BranchEmulator branchEmulator;

		public BlockCflowDeobfuscator() {
			instructionEmulator = new InstructionEmulator();
			branchEmulator = new BranchEmulator(instructionEmulator, this);
		}

		protected override bool Deobfuscate(Block block) {
			this.block = block;
			Debug.Assert(blocks != null);
			Debug.Assert(allBlocks != null);
			if (!block.LastInstr.IsConditionalBranch() && block.LastInstr.OpCode.Code != Code.Switch)
				return false;
			instructionEmulator.Initialize(blocks, allBlocks[0] == block);

			var instructions = block.Instructions;
			if (instructions.Count == 0)
				return false;
			try {
				for (int i = 0; i < instructions.Count - 1; i++) {
					var instr = instructions[i].Instruction;
					instructionEmulator.Emulate(instr);
				}
			}
			catch (NullReferenceException) {
				// Here if eg. invalid metadata token in a call instruction (operand is null)
				return false;
			}

			return branchEmulator.Emulate(block.LastInstr.Instruction);
		}

		void PopPushedArgs(int stackArgs) {
			Debug.Assert(block != null);
			// Pop the arguments to the bcc instruction. The dead code remover will get rid of the
			// pop and any pushed arguments. Insert the pops just before the bcc instr.
			for (int i = 0; i < stackArgs; i++)
				block.Insert(block.Instructions.Count - 1, OpCodes.Pop.ToInstruction());
		}

		void IBranchHandler.HandleNormal(int stackArgs, bool isTaken) {
			Debug.Assert(block != null);
			PopPushedArgs(stackArgs);
			block.ReplaceBccWithBranch(isTaken);
		}

		/// <summary>
		///     Folding a <c>switch</c> whose operand this pass computed is dispatch resolution too, so
		///     it has to answer to the same switch. Without this the "unresolved" candidate built by
		///     the caller comes out byte-identical to the resolved one and the selection reports a
		///     save it never made.
		/// </summary>
		public bool SuppressDispatchResolution { get; set; }

		bool IBranchHandler.HandleSwitch(Int32Value switchIndex) {
			Debug.Assert(block != null);
			if (SuppressDispatchResolution)
				return false;
			var target = CflowUtils.GetSwitchTarget(block.Targets, block.FallThrough, switchIndex);
			if (target == null)
				return false;

			PopPushedArgs(1);
			block.ReplaceSwitchWithBranch(target);
			return true;
		}
	}
}
