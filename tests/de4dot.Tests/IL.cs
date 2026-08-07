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

namespace de4dot.tests {
	/// <summary>
	///     Builds throwaway in-memory assemblies to run passes against.
	///
	///     Everything here is a <c>ModuleDefUser</c> rather than a file on disk: the layer under test
	///     reads dnlib objects, so assembling real IL would only add an external assembler to the
	///     dependency list and a temp directory to clean up, without testing anything extra. Fixtures
	///     that genuinely need a written assembly live under tests/samples/ instead.
	/// </summary>
	static class IL {
		public static ModuleDefUser Module() =>
			new ModuleDefUser("test", Guid.NewGuid(), new AssemblyRefUser(new AssemblyNameInfo("mscorlib")));

		public static TypeDefUser AddType(ModuleDef module, string name = "C") {
			var type = new TypeDefUser("", name, module.CorLibTypes.Object.TypeDefOrRef);
			module.Types.Add(type);
			return type;
		}

		/// <summary>
		///     A static method with an empty body, N int32 parameters and M int32 locals, plus
		///     <paramref name="int32ArrayLocals"/> locals typed <c>int32[]</c>.
		///
		///     The array-typed locals matter: the emulator truncates on <c>stloc</c> to the local's
		///     declared type, so stashing an array reference in an <c>int32</c> local correctly
		///     discards it. A test that wants to alias an array through a local has to declare the
		///     local's type honestly.
		/// </summary>
		public static MethodDefUser StaticMethod(ModuleDef module, int int32Params = 0, int int32Locals = 0,
				string name = "M", TypeDef declaringType = null, int int32ArrayLocals = 0) {
			var paramTypes = new TypeSig[int32Params];
			for (int i = 0; i < int32Params; i++)
				paramTypes[i] = module.CorLibTypes.Int32;

			var method = new MethodDefUser(name,
				MethodSig.CreateStatic(module.CorLibTypes.Void, paramTypes),
				MethodAttributes.Public | MethodAttributes.Static);
			method.Body = new CilBody { InitLocals = true };
			for (int i = 0; i < int32Locals; i++)
				method.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
			for (int i = 0; i < int32ArrayLocals; i++)
				method.Body.Variables.Add(new Local(new SZArraySig(module.CorLibTypes.Int32)));

			(declaringType ?? AddType(module)).Methods.Add(method);
			method.Parameters.UpdateParameterTypes();
			return method;
		}

		public static void SetBody(MethodDef method, params Instruction[] instructions) {
			method.Body.Instructions.Clear();
			foreach (var instr in instructions)
				method.Body.Instructions.Add(instr);
			method.Body.UpdateInstructionOffsets();
		}

		public static Instruction Ldc(int value) => Instruction.CreateLdcI4(value);
		public static Instruction Newarr(ModuleDef module) =>
			OpCodes.Newarr.ToInstruction(module.CorLibTypes.Int32.TypeDefOrRef);
		public static Instruction NewarrOf(ITypeDefOrRef elementType) => OpCodes.Newarr.ToInstruction(elementType);
		public static Instruction Op(OpCode opCode) => opCode.ToInstruction();
		public static Instruction Ldloc(Local local) => OpCodes.Ldloc.ToInstruction(local);
		public static Instruction Stloc(Local local) => OpCodes.Stloc.ToInstruction(local);
		public static Instruction Ldarg(MethodDef method, int index) =>
			OpCodes.Ldarg.ToInstruction(method.Parameters[index]);

		public static List<Instruction> List(params Instruction[] instructions) =>
			new List<Instruction>(instructions);
	}
}
