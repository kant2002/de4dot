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

using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.blocks;
using de4dot.blocks.cflow;
using Xunit;

namespace de4dot.tests {
	/// <summary>
	///     <see cref="StateMachineTracer"/> verdicts.
	///
	///     The whole value of this component rests on one asymmetry: it explores an OVER-approximation
	///     of the reachable configurations, so finding no exit anywhere proves no execution can exit
	///     (<c>Loops</c>), while finding an exit proves nothing (<c>ExitReachable</c> is named to say
	///     so). Every test here defends that asymmetry, because the caller acts on <c>Loops</c> by
	///     throwing a rewrite away — so a <c>Loops</c> reached by NARROWING the set silently discards
	///     correct deobfuscation.
	/// </summary>
	public class StateMachineTracerTests {
		static StateMachineVerdict Trace(MethodDef method) =>
			StateMachineTracer.Trace(new Blocks(method), method).Verdict;

		static void AssertNotLoops(MethodDef method, string why) {
			var verdict = Trace(method);
			Assert.True(verdict != StateMachineVerdict.Loops,
				$"got Loops, but {why}. A Loops verdict is treated as a proof and discards the rewrite.");
		}

		/// <summary>
		///     A dispatch that selects a returning arm. The baseline: the tracer must not call this
		///     non-terminating.
		/// </summary>
		[Fact]
		public void ADispatchSelectingAReturningArmIsNotLoops() {
			var module = IL.Module();
			var method = IL.StaticMethod(module, int32Locals: 1);
			var local = method.Body.Variables[0];

			var ret = OpCodes.Ret.ToInstruction();
			var spin = OpCodes.Nop.ToInstruction();
			var dispatch = OpCodes.Ldloc.ToInstruction(local);
			var sw = OpCodes.Switch.ToInstruction(new[] { spin, ret });

			IL.SetBody(method,
				IL.Ldc(1), IL.Stloc(local), OpCodes.Br.ToInstruction(dispatch),
				dispatch, sw,
				spin, OpCodes.Br.ToInstruction(dispatch),
				ret);
			AssertNotLoops(method, "state 1 selects the arm that returns");
		}

		[Fact]
		public void AGenuinelyNonTerminatingMachineIsLoops() {
			var module = IL.Module();
			var method = IL.StaticMethod(module, int32Locals: 1);
			var local = method.Body.Variables[0];

			var spin = OpCodes.Nop.ToInstruction();
			var ret = OpCodes.Ret.ToInstruction();
			var dispatch = OpCodes.Ldloc.ToInstruction(local);
			var sw = OpCodes.Switch.ToInstruction(new[] { spin, ret });

			// state 0 forever: the exit arm exists in the graph but nothing dispatches to it.
			IL.SetBody(method,
				IL.Ldc(0), IL.Stloc(local), OpCodes.Br.ToInstruction(dispatch),
				dispatch, sw,
				spin, IL.Ldc(0), IL.Stloc(local), OpCodes.Br.ToInstruction(dispatch),
				ret);
			Assert.Equal(StateMachineVerdict.Loops, Trace(method));
		}

		/// <summary>
		///     The state variable lives above the tracer's tracked-locals ceiling. Locals it does not
		///     track must read as UNKNOWN — if they read as a fabricated zero, the switch prunes to
		///     arm 0 and the machine looks non-terminating.
		/// </summary>
		[Fact]
		public void AStateVariableAboveTheTrackedLocalCeilingIsNotLoops() {
			var module = IL.Module();
			var method = IL.StaticMethod(module, int32Locals: 20);
			var state = method.Body.Variables[17];

			var spin = OpCodes.Nop.ToInstruction();
			var ret = OpCodes.Ret.ToInstruction();
			var dispatch = OpCodes.Ldloc.ToInstruction(state);
			var sw = OpCodes.Switch.ToInstruction(new[] { spin, ret });

			IL.SetBody(method,
				IL.Ldc(1), IL.Stloc(state), OpCodes.Br.ToInstruction(dispatch),
				dispatch, sw,
				spin, OpCodes.Br.ToInstruction(dispatch),
				ret);
			AssertNotLoops(method, "the state variable holds 1 and selects the returning arm; " +
				"locals beyond the tracked ceiling must be unknown, never a fabricated zero");
		}

		/// <summary>
		///     A conditional branch's operands must leave the modelled stack when the branch is
		///     stepped over, exactly as the switch operand does. Leaving them behind shifts every
		///     successor's stack, so a later switch reads the wrong slot as its operand.
		/// </summary>
		[Fact]
		public void AConditionalBranchDoesNotLeaveItsOperandOnTheStack() {
			var module = IL.Module();
			var method = IL.StaticMethod(module);

			var spin = IL.Ldc(0);
			var ret = OpCodes.Ret.ToInstruction();
			var dispatch = OpCodes.Switch.ToInstruction(new[] { spin, ret });

			// Push the dispatch operand 1, then an opaque predicate the branch consumes. If the
			// predicate is still on the modelled stack at the switch, the operand reads as 0 -> arm 0.
			// Arm 0 re-pushes, so the leaked slot persists and the configuration closes without ever
			// reaching the returning arm. (A spin arm that pushes nothing would let the stack shrink
			// back and mask the leak, which is why this shape is the one that catches it.)
			IL.SetBody(method,
				IL.Ldc(1),
				IL.Ldc(0), OpCodes.Brtrue.ToInstruction(dispatch),
				dispatch,
				spin, OpCodes.Br.ToInstruction(dispatch),
				ret);
			AssertNotLoops(method, "the dispatch operand is 1 once the branch has consumed its own " +
				"predicate, so the returning arm is selected");
		}

		[Fact]
		public void AnUnknownSwitchOperandExploresEveryArm() {
			var module = IL.Module();
			var method = IL.StaticMethod(module, int32Params: 1);

			var spin = OpCodes.Nop.ToInstruction();
			var ret = OpCodes.Ret.ToInstruction();
			var dispatch = OpCodes.Switch.ToInstruction(new[] { spin, ret });

			IL.SetBody(method,
				IL.Ldarg(method, 0), dispatch,
				spin, OpCodes.Br.ToInstruction(ret),
				ret);
			AssertNotLoops(method, "an unknown operand must widen to every arm, one of which returns");
		}
	}
}
