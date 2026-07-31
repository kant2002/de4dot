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
using de4dot.blocks.cflow;
using Xunit;

namespace de4dot.tests {
	/// <summary>
	///     The emulator's <c>newarr</c>/<c>stelem</c>/<c>ldelem</c> element tracking.
	///
	///     Why this is worth its own file: a value this code reports as a known constant flows into
	///     <c>SwitchCflowDeobfuscator.GetSwitchTarget</c>, which picks a switch arm from it. A
	///     fabricated constant therefore rewrites a method to branch the wrong way, and the result is
	///     type-safe, stack-balanced, non-empty IL — so no verifier and no structural gate can catch
	///     it downstream. The invariant these tests defend is that the emulator never reports a
	///     constant real execution could contradict; being less precise is always allowed.
	/// </summary>
	public class InstructionEmulatorArrayTests {
		static Value RunAndPop(MethodDef method, params Instruction[] instructions) {
			var emulator = new InstructionEmulator();
			emulator.Initialize(method, false);
			foreach (var instr in instructions)
				emulator.Emulate(instr);
			return emulator.Pop();
		}

		static bool IsKnown(Value value, out int result) {
			result = 0;
			if (value is Int32Value int32 && int32.AllBitsValid()) {
				result = int32.Value;
				return true;
			}
			return false;
		}

		static void AssertKnown(int expected, Value value) {
			Assert.True(IsKnown(value, out int actual), $"expected the known constant {expected}, got {value}");
			Assert.Equal(expected, actual);
		}

		static void AssertUnknown(Value value) {
			Assert.False(IsKnown(value, out int actual),
				$"expected an unknown value, got the constant {actual} — the emulator is claiming to know " +
				"something real execution can contradict");
		}

		[Fact]
		public void AModelledStoreIsReadBack() {
			var module = IL.Module();
			var method = IL.StaticMethod(module);
			AssertKnown(5, RunAndPop(method,
				IL.Ldc(2), IL.Newarr(module),
				IL.Op(OpCodes.Dup), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4)));
		}

		[Fact]
		public void UnwrittenElementsAreZeroBecauseNewarrZeroInitializes() {
			var module = IL.Module();
			var method = IL.StaticMethod(module);
			AssertKnown(0, RunAndPop(method,
				IL.Ldc(4), IL.Newarr(module),
				IL.Ldc(2), IL.Op(OpCodes.Ldelem_I4)));
		}

		/// <summary>
		///     The regression test for the defect this tracking shipped with: a store the emulator
		///     could not place was skipped instead of invalidating, so the element kept the value an
		///     earlier store had put there and was read back as fact.
		/// </summary>
		[Fact]
		public void AStoreAtAnUnknownIndexInvalidatesTheWholeArray() {
			var module = IL.Module();
			var method = IL.StaticMethod(module, int32Params: 1);
			AssertUnknown(RunAndPop(method,
				IL.Ldc(2), IL.Newarr(module),
				IL.Op(OpCodes.Dup), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				// arr[<unknown>] = 9. Real execution may put 9 at index 0, so index 0 is no longer known.
				IL.Op(OpCodes.Dup), IL.Ldarg(method, 0), IL.Ldc(9), IL.Op(OpCodes.Stelem_I4),
				IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4)));
		}

		/// <summary>
		///     An unknown VALUE at a KNOWN index is fully modellable — that element becomes unknown
		///     and its neighbours are untouched. Only an unknown INDEX forces wholesale invalidation,
		///     because then there is no way to say which element moved.
		/// </summary>
		[Fact]
		public void AnUnknownValueAtAKnownIndexOnlyClobbersThatElement() {
			var module = IL.Module();

			var readOther = IL.StaticMethod(module, int32Params: 1);
			AssertKnown(5, RunAndPop(readOther,
				IL.Ldc(2), IL.Newarr(module),
				IL.Op(OpCodes.Dup), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				IL.Op(OpCodes.Dup), IL.Ldc(1), IL.Ldarg(readOther, 0), IL.Op(OpCodes.Stelem_I4),
				IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4)));

			var readClobbered = IL.StaticMethod(module, int32Params: 1, name: "M2");
			AssertUnknown(RunAndPop(readClobbered,
				IL.Ldc(2), IL.Newarr(module),
				IL.Op(OpCodes.Dup), IL.Ldc(1), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				IL.Op(OpCodes.Dup), IL.Ldc(1), IL.Ldarg(readClobbered, 0), IL.Op(OpCodes.Stelem_I4),
				IL.Ldc(1), IL.Op(OpCodes.Ldelem_I4)));
		}

		[Fact]
		public void AnOutOfRangeStoreInvalidatesRatherThanBeingIgnored() {
			var module = IL.Module();
			var method = IL.StaticMethod(module);
			AssertUnknown(RunAndPop(method,
				IL.Ldc(2), IL.Newarr(module),
				IL.Op(OpCodes.Dup), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				IL.Op(OpCodes.Dup), IL.Ldc(7), IL.Ldc(9), IL.Op(OpCodes.Stelem_I4),
				IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4)));
		}

		[Fact]
		public void ReadingOutOfRangeIsUnknownRatherThanThrowing() {
			var module = IL.Module();
			var method = IL.StaticMethod(module);
			AssertUnknown(RunAndPop(method,
				IL.Ldc(2), IL.Newarr(module),
				IL.Ldc(11), IL.Op(OpCodes.Ldelem_I4)));
		}

		[Fact]
		public void AnUnknownIndexReadsUnknownEvenFromAFullyKnownArray() {
			var module = IL.Module();
			var method = IL.StaticMethod(module, int32Params: 1);
			AssertUnknown(RunAndPop(method,
				IL.Ldc(2), IL.Newarr(module),
				IL.Op(OpCodes.Dup), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				IL.Ldarg(method, 0), IL.Op(OpCodes.Ldelem_I4)));
		}

		/// <summary>
		///     A tracked array reached through a local is the same array. Real IL aliases it, so a
		///     store seen through one reference must be visible through the other.
		/// </summary>
		[Fact]
		public void StoresThroughALocalAliasTheSameArray() {
			var module = IL.Module();
			var method = IL.StaticMethod(module, int32ArrayLocals: 1);
			var local = method.Body.Variables[0];
			AssertKnown(5, RunAndPop(method,
				IL.Ldc(2), IL.Newarr(module), IL.Stloc(local),
				IL.Ldloc(local), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				IL.Ldloc(local), IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4)));
		}

		[Fact]
		public void AnInvalidationThroughOneAliasIsVisibleThroughTheOther() {
			var module = IL.Module();
			var method = IL.StaticMethod(module, int32Params: 1, int32ArrayLocals: 1);
			var local = method.Body.Variables[0];
			AssertUnknown(RunAndPop(method,
				IL.Ldc(2), IL.Newarr(module), IL.Stloc(local),
				IL.Ldloc(local), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				IL.Ldloc(local), IL.Ldarg(method, 0), IL.Ldc(9), IL.Op(OpCodes.Stelem_I4),
				IL.Ldloc(local), IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4)));
		}

		/// <summary>
		///     Only 4-byte integer arrays are tracked. A narrower element type would truncate on
		///     store, so tracking one would report a value the array cannot actually hold.
		/// </summary>
		[Fact]
		public void ANonInt32ArrayIsNotTracked() {
			var module = IL.Module();
			var method = IL.StaticMethod(module);
			AssertUnknown(RunAndPop(method,
				IL.Ldc(2), IL.NewarrOf(module.CorLibTypes.Byte.TypeDefOrRef),
				IL.Op(OpCodes.Dup), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I1),
				IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4)));
		}

		[Fact]
		public void ANarrowingStoreIntoATrackedArrayInvalidatesIt() {
			var module = IL.Module();
			var method = IL.StaticMethod(module);
			AssertUnknown(RunAndPop(method,
				IL.Ldc(2), IL.Newarr(module),
				IL.Op(OpCodes.Dup), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				// stelem.i1 through an int32[] reference is not something the tracking models.
				IL.Op(OpCodes.Dup), IL.Ldc(1), IL.Ldc(0x1FF), IL.Op(OpCodes.Stelem_I1),
				IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4)));
		}

		[Fact]
		public void AnArrayWithAnUnknownLengthIsNotTracked() {
			var module = IL.Module();
			var method = IL.StaticMethod(module, int32Params: 1);
			AssertUnknown(RunAndPop(method,
				IL.Ldarg(method, 0), IL.Newarr(module),
				IL.Op(OpCodes.Dup), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4)));
		}

		/// <summary>
		///     A tracked array must not survive into the next method the emulator is pointed at. It is
		///     mutated in place, so a leak would let one method's stores be read back in another.
		/// </summary>
		[Fact]
		public void ATrackedArrayDoesNotSurviveReinitialisation() {
			var module = IL.Module();
			var method = IL.StaticMethod(module, int32Locals: 1);
			var local = method.Body.Variables[0];

			var emulator = new InstructionEmulator();
			emulator.Initialize(method, false);
			foreach (var instr in new[] {
					IL.Ldc(2), IL.Newarr(module), IL.Stloc(local),
					IL.Ldloc(local), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4) })
				emulator.Emulate(instr);

			emulator.Initialize(method, false);
			foreach (var instr in new[] { IL.Ldloc(local), IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4) })
				emulator.Emulate(instr);

			AssertUnknown(emulator.Pop());
		}
	}
}
