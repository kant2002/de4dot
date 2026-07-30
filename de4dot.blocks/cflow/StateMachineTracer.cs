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
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.blocks.cflow {
	public enum StateMachineVerdict {
		/// <summary>The traced state sequence reaches a ret/throw. Faithful, however verbose.</summary>
		Terminates,

		/// <summary>
		///     The traced state sequence revisits a state without ever reaching an exit, i.e. the
		///     emitted method loops forever. Some switch dispatch was resolved to the wrong target.
		/// </summary>
		Loops,

		/// <summary>Not decidable: a transition or condition is not a known constant. Not judged.</summary>
		Undecidable,
	}

	public class StateMachineTrace {
		public StateMachineVerdict Verdict { get; set; }

		/// <summary>Switch operand values in the order the trace saw them.</summary>
		public List<int> States { get; } = new List<int>();

		/// <summary>Offset of the switch that could not be decided, or that closed the cycle.</summary>
		public uint SwitchOffset { get; set; }
	}

	/// <summary>
	///     Traces a method's switch-dispatch state machine and reports whether it terminates.
	///
	///     Why this exists as a separate check
	///     -----------------------------------
	///     A switch dispatch that was resolved to the WRONG target does not produce invalid IL, an
	///     empty body, an unbalanced stack, or a body without a ret. It produces a method that is
	///     perfectly verifiable and loops forever, with the `ret` still present as a switch target so
	///     it stays reachable in the control-flow graph even though no execution ever dispatches to
	///     it. Every other gate is therefore blind to it, and the damage is invisible in the output:
	///     the method just looks *shorter*, because every statement on the unreachable tail is gone.
	///
	///     Only following the state variable from its seed reveals it, which is what this does.
	///
	///     Deliberately conservative
	///     -------------------------
	///     Anything it cannot decide is <see cref="StateMachineVerdict.Undecidable"/>, never
	///     <see cref="StateMachineVerdict.Loops"/>. A Loops verdict is meant to be trustworthy enough
	///     to act on -- to fail a build, or to reject a rewrite -- so it must never be a guess.
	///     Conditional branches whose condition is not a known constant, switch operands that are not
	///     known constants, and traces that exceed the step budget all yield Undecidable.
	/// </summary>
	public static class StateMachineTracer {
		const int maxSteps = 512;

		/// <summary>
		///     Trace <paramref name="method"/>'s dispatch machine from the method entry.
		///
		///     One emulator is carried across the whole walk on purpose: in the shape this is meant to
		///     catch, the state is carried on the **evaluation stack** (each predecessor pushes a
		///     constant that the shared `switch` pops), not in a local. Re-initialising per block would
		///     lose exactly the value being traced.
		/// </summary>
		public static StateMachineTrace Trace(Blocks blocks, MethodDef method) {
			var result = new StateMachineTrace { Verdict = StateMachineVerdict.Undecidable };
			var all = blocks.MethodBlocks.GetAllBlocks();
			if (all.Count == 0)
				return result;

			var emu = new InstructionEmulator();
			emu.Initialize(method, true);

			// (switch block, operand value) is a complete description of the machine's state at a
			// dispatch: the continuation from there is deterministic, so seeing the same pair twice
			// means the machine cannot make further progress.
			var seenAtSwitch = new HashSet<(Block, int)>();
			var current = all[0];

			for (int step = 0; step < maxSteps; step++) {
				if (current is null)
					return result;

				var instrs = current.Instructions;
				int end = instrs.Count;

				// A block that can end the method ends the trace: an exit is genuinely reached.
				foreach (var instr in instrs) {
					switch (instr.OpCode.Code) {
					case Code.Ret:
					case Code.Throw:
					case Code.Rethrow:
						result.Verdict = StateMachineVerdict.Terminates;
						return result;
					}
				}

				bool endsInSwitch = end > 0 && instrs[end - 1].OpCode.Code == Code.Switch;
				bool endsInBranch = end > 0 && (instrs[end - 1].IsBr() || instrs[end - 1].IsConditionalBranch());
				int emulateTo = endsInSwitch || endsInBranch ? end - 1 : end;

				try {
					emu.Emulate(instrs, 0, emulateTo);
				}
				catch {
					return result; // emulation failed: undecidable, and that is the safe answer
				}

				if (endsInSwitch) {
					result.SwitchOffset = instrs[end - 1].Instruction?.Offset ?? 0;
					if (emu.StackSize() < 1)
						return result;
					if (emu.Pop() is not Int32Value iv || !iv.AllBitsValid())
						return result; // operand not a known constant

					int state = iv.Value;
					result.States.Add(state);

					var targets = current.Targets;
					if (targets is null || state < 0 || state >= targets.Count) {
						// Out of range means the default (fall-through) target is taken.
						current = current.FallThrough;
						continue;
					}

					if (!seenAtSwitch.Add((current, state))) {
						// Same dispatch, same operand, and no exit seen on the way here.
						result.Verdict = StateMachineVerdict.Loops;
						return result;
					}

					current = targets[state];
					continue;
				}

				if (endsInBranch && instrs[end - 1].IsConditionalBranch())
					return result; // condition not folded: undecidable rather than a guess

				current = current.GetOnlyTarget();
			}

			return result; // step budget exhausted
		}
	}
}
