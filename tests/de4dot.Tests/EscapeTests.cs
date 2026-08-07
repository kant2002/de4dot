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
	///     A tracked array handed to anything the emulator does not model.
	///
	///     <c>stelem</c> is not the only way an array's contents change. Passing the reference to a
	///     call, or taking an element address with <c>ldelema</c> and writing through it, mutates the
	///     array without any <c>stelem</c> being emulated. If the tracked element list survives that,
	///     its stale contents are handed back as all-bits-valid constants — the same failure as a
	///     dropped store, reached by a different route.
	///
	///     This matters far beyond obfuscated input: <c>call RuntimeHelpers::InitializeArray</c> is
	///     how the C# compiler itself emits <c>int[] x = { ... }</c> once the initializer has a few
	///     elements, so the shape occurs in ordinary assemblies.
	/// </summary>
	public class TrackedArrayEscapeTests {
		static MethodDef Consumer(ModuleDef module, TypeDef type, string name) {
			var consumer = new MethodDefUser(name,
				MethodSig.CreateStatic(module.CorLibTypes.Void, new SZArraySig(module.CorLibTypes.Int32)),
				MethodAttributes.Public | MethodAttributes.Static);
			type.Methods.Add(consumer);
			return consumer;
		}

		static Value RunAndPop(MethodDef method, params Instruction[] instructions) {
			var emulator = new InstructionEmulator();
			emulator.Initialize(method, false);
			foreach (var instr in instructions)
				emulator.Emulate(instr);
			return emulator.Pop();
		}

		static void AssertUnknown(Value value, string what) {
			if (value is Int32Value int32 && int32.AllBitsValid())
				Assert.Fail($"{what}: emulator reports the constant {int32.Value}, but the array may have " +
					"been rewritten by code it did not model");
		}

		[Fact]
		public void PassingTheArrayToACallInvalidatesIt() {
			var module = IL.Module();
			var type = IL.AddType(module);
			var method = IL.StaticMethod(module, declaringType: type);
			var fill = Consumer(module, type, "Fill");

			AssertUnknown(RunAndPop(method,
				IL.Ldc(2), IL.Newarr(module),
				IL.Op(OpCodes.Dup), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				// Fill() may write any element. Nothing here models what it does.
				IL.Op(OpCodes.Dup), OpCodes.Call.ToInstruction(fill),
				IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4)),
				"after passing the array to a call");
		}

		[Fact]
		public void WritingThroughLdelemaInvalidatesTheArray() {
			var module = IL.Module();
			var method = IL.StaticMethod(module, int32ArrayLocals: 1);
			var local = method.Body.Variables[0];

			AssertUnknown(RunAndPop(method,
				IL.Ldc(2), IL.Newarr(module), IL.Stloc(local),
				IL.Ldloc(local), IL.Ldc(0), IL.Ldc(5), IL.Op(OpCodes.Stelem_I4),
				// &arr[0] then stind.i4 writes element 0 without any stelem being seen.
				IL.Ldloc(local), IL.Ldc(0),
				OpCodes.Ldelema.ToInstruction(module.CorLibTypes.Int32.TypeDefOrRef),
				IL.Ldc(9), IL.Op(OpCodes.Stind_I4),
				IL.Ldloc(local), IL.Ldc(0), IL.Op(OpCodes.Ldelem_I4)),
				"after writing through ldelema/stind.i4");
		}

		[Fact]
		public void AnUnwrittenElementIsNotAssumedZeroAfterAnEscape() {
			var module = IL.Module();
			var type = IL.AddType(module);
			var method = IL.StaticMethod(module, declaringType: type);
			var fill = Consumer(module, type, "Fill");

			// The zero-seed is only sound while every mutation is seen. This is the InitializeArray
			// shape: nothing is ever stored via stelem, so every element still reads as 0.
			AssertUnknown(RunAndPop(method,
				IL.Ldc(8), IL.Newarr(module),
				IL.Op(OpCodes.Dup), OpCodes.Call.ToInstruction(fill),
				IL.Ldc(2), IL.Op(OpCodes.Ldelem_I4)),
				"after a call that may initialize the array");
		}
	}
}
