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
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.blocks.cflow {
	public enum StateMachineVerdict {
		/// <summary>
		///     An exit is reachable somewhere in the over-approximated machine.
		///
		///     Deliberately NOT called Terminates, because it does not prove that. Exploration widens
		///     on every imprecision — an unknown switch operand takes every target — so reaching a
		///     `ret` down one of those targets says only that an exit exists in a machine that is a
		///     superset of the real one. Another explored target may loop forever. This is the absence
		///     of a non-termination proof, not the presence of a termination proof, and a count of
		///     these must not be read as "this many methods terminate".
		/// </summary>
		ExitReachable,

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
	///     Why it is an over-approximation, not a walk
	///     -------------------------------------------
	///     This used to follow a single concrete path and give up at the first condition it could not
	///     fold — which is any real `brfalse` on a field. Real machines interleave dispatch with
	///     ordinary conditionals, so that answered Undecidable for exactly the methods worth judging,
	///     and two whose exit case was unreachable sat in that bucket while every gate passed.
	///
	///     So it explores a bounded set of *configurations* (block + evaluation stack + tracked
	///     locals) instead, and every imprecision widens the set rather than ending the walk: an
	///     unknown branch condition takes both successors, an unknown switch operand takes every
	///     target, and an instruction that will not emulate contributes all successors with everything
	///     unknown. That direction is the whole point. The reachable set is a superset of what can
	///     really happen, so **if no exit appears anywhere in it, no execution can exit** — which makes
	///     a Loops verdict a proof rather than a guess. The cost is the reverse: imprecision can make a
	///     genuinely looping method look terminating, and missing a bad resolution is the failure this
	///     is allowed to have. Rejecting a good one is not.
	///
	///     Undecidable therefore means one thing only: a budget ran out, so the set was never
	///     completed and neither conclusion is available.
	/// </summary>
	public static class StateMachineTracer {
		// Caps. Exhausting any of them yields Undecidable rather than a conclusion drawn from a partial
		// exploration, which would be a guess wearing a proof's clothes.
		const int MaxConfigurations = 4096;
		const int MaxStackDepth = 16;
		const int MaxTrackedLocals = 16;

		/// <summary>An abstract evaluation stack/local slot: a known int32, or unknown.</summary>
		readonly struct Slot {
			public readonly int Value;
			public readonly bool Known;
			Slot(int value, bool known) { Value = value; Known = known; }
			public static readonly Slot Unknown = new Slot(0, false);
			public static Slot Of(int value) => new Slot(value, true);
			public override string ToString() => Known ? Value.ToString() : "?";
		}

		sealed class Configuration {
			public readonly Block Block;
			public readonly Slot[] Stack;
			public readonly Slot[] Locals;
			public readonly string Key;

			public Configuration(Block block, Slot[] stack, Slot[] locals) {
				Block = block;
				Stack = stack;
				Locals = locals;
				Key = block.GetHashCode() + "|" + string.Join(",", stack) + "|" + string.Join(",", locals);
			}
		}

		/// <summary>
		///     Trace <paramref name="method"/>'s dispatch machine from the method entry.
		/// </summary>
		public static StateMachineTrace Trace(Blocks blocks, MethodDef method) {
			var result = new StateMachineTrace { Verdict = StateMachineVerdict.Undecidable };
			var all = blocks.MethodBlocks.GetAllBlocks();
			if (all.Count == 0)
				return result;

			int localCount = method.Body?.Variables?.Count ?? 0;
			if (localCount > MaxTrackedLocals)
				localCount = MaxTrackedLocals;

			var seen = new HashSet<string>();
			var work = new Stack<Configuration>();
			void Schedule(Block block, Slot[] stack, Slot[] locals) {
				if (block is null || stack.Length > MaxStackDepth)
					return;
				var config = new Configuration(block, stack, locals);
				if (seen.Add(config.Key))
					work.Push(config);
			}

			var noLocals = new Slot[localCount];
			for (int i = 0; i < localCount; i++)
				noLocals[i] = Slot.Unknown;
			Schedule(all[0], new Slot[0], noLocals);

			int explored = 0;
			while (work.Count > 0) {
				if (++explored > MaxConfigurations)
					return result;			// never completed the set: Undecidable, not a conclusion

				var config = work.Pop();
				var instrs = config.Block.Instructions;
				int end = instrs.Count;

				// An exit anywhere in the reachable set ends the search: it rules OUT the only
				// verdict that can be proven here. It does not establish that the method terminates.
				bool exits = false;
				foreach (var instr in instrs) {
					switch (instr.OpCode.Code) {
					case Code.Ret:
					case Code.Throw:
					case Code.Rethrow:
						exits = true;
						break;
					}
					if (exits)
						break;
				}
				if (exits) {
					result.Verdict = StateMachineVerdict.ExitReachable;
					return result;
				}

				bool endsInSwitch = end > 0 && instrs[end - 1].OpCode.Code == Code.Switch;
				bool endsInBranch = end > 0 && (instrs[end - 1].IsBr() || instrs[end - 1].IsConditionalBranch());
				int emulateTo = endsInSwitch || endsInBranch ? end - 1 : end;

				Slot[] outStack, outLocals;
				bool emulated = TryEmulate(method, config, instrs, emulateTo, localCount,
					out outStack, out outLocals);
				if (!emulated) {
					// Could not model it. Widen instead of stopping: everything unknown, every
					// successor scheduled. That keeps the set an over-approximation, which is the only
					// property a Loops verdict depends on.
					ScheduleAllSuccessors(config.Block, Schedule, new Slot[0], AllUnknown(localCount));
					continue;
				}

				if (endsInSwitch) {
					result.SwitchOffset = instrs[end - 1].Instruction?.Offset ?? 0;
					var targets = config.Block.Targets;
					var operand = outStack.Length > 0 ? outStack[outStack.Length - 1] : Slot.Unknown;
					var rest = outStack.Length > 0 ? Take(outStack, outStack.Length - 1) : outStack;

					if (!operand.Known || targets is null) {
						// Unbounded operand: every target stays possible, so every target is taken.
						ScheduleAllSuccessors(config.Block, Schedule, rest, outLocals);
						continue;
					}
					int state = operand.Value;
					result.States.Add(state);
					if (state < 0 || state >= targets.Count)
						Schedule(config.Block.FallThrough, rest, outLocals);
					else
						Schedule(targets[state], rest, outLocals);
					continue;
				}

				if (endsInBranch && instrs[end - 1].IsConditionalBranch()) {
					// The condition is not tracked here -- only int32 dataflow is -- so both sides are
					// live. Over-approximating a two-way branch is cheap and keeps the proof valid.
					ScheduleAllSuccessors(config.Block, Schedule, outStack, outLocals);
					continue;
				}

				Schedule(config.Block.GetOnlyTarget(), outStack, outLocals);
			}

			// The set is complete and contains no exit. Nothing the method can do returns.
			result.Verdict = StateMachineVerdict.Loops;
			return result;
		}

		static Slot[] AllUnknown(int count) {
			var slots = new Slot[count];
			for (int i = 0; i < count; i++)
				slots[i] = Slot.Unknown;
			return slots;
		}

		static Slot[] Take(Slot[] source, int count) {
			var slots = new Slot[count];
			for (int i = 0; i < count; i++)
				slots[i] = source[i];
			return slots;
		}

		static void ScheduleAllSuccessors(Block block, Action<Block, Slot[], Slot[]> schedule,
				Slot[] stack, Slot[] locals) {
			if (block.Targets is not null) {
				foreach (var target in block.Targets)
					schedule(target, stack, locals);
			}
			schedule(block.FallThrough, stack, locals);
		}

		/// <summary>
		///     Run one block from <paramref name="config"/>'s incoming state and read the outgoing
		///     stack and locals back out. False means the block could not be modelled at all.
		/// </summary>
		static bool TryEmulate(MethodDef method, Configuration config, IList<Instr> instrs, int emulateTo,
				int localCount, out Slot[] outStack, out Slot[] outLocals) {
			outStack = new Slot[0];
			outLocals = AllUnknown(localCount);
			try {
				var emu = new InstructionEmulator();
				emu.Initialize(method, true);
				emu.ClearStack();
				foreach (var slot in config.Stack)
					emu.Push(slot.Known ? new Int32Value(slot.Value) : (Value)Int32Value.CreateUnknown());
				for (int i = 0; i < localCount; i++) {
					var local = method.Body.Variables[i];
					if (config.Locals[i].Known)
						emu.SetLocal(local, new Int32Value(config.Locals[i].Value));
					else
						emu.MakeLocalUnknown(local);
				}

				emu.Emulate(instrs, 0, emulateTo);

				int depth = emu.StackSize();
				if (depth > MaxStackDepth)
					return false;
				var stack = new Slot[depth];
				// Pop reverses, so fill from the top down and hand back bottom-first.
				for (int i = depth - 1; i >= 0; i--)
					stack[i] = ToSlot(emu.Pop());
				outStack = stack;

				var locals = new Slot[localCount];
				for (int i = 0; i < localCount; i++)
					locals[i] = ToSlot(emu.GetLocal(method.Body.Variables[i]));
				outLocals = locals;
				return true;
			}
			catch {
				return false;
			}
		}

		static Slot ToSlot(Value value) =>
			value is Int32Value iv && iv.AllBitsValid() ? Slot.Of(iv.Value) : Slot.Unknown;
	}
}
