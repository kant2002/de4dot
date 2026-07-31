using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using de4dot.blocks.cflow;
using dnlib.DotNet;

namespace de4dot.blocks.tests {
	internal class InstructionVerification {

		public static void ValidateOpCode(int a, int b, OpCode opCode) {
			Type[] methodArgs = { typeof(int), typeof(int) };
			var module = typeof(InstructionVerification).Module;
			DynamicMethod validateInstruction = new DynamicMethod(
				"ValidateInstruction",
				typeof(int),
				methodArgs,
				module
			);
			ILGenerator il = validateInstruction.GetILGenerator();
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(opCode);
			il.Emit(OpCodes.Ret);

			ModuleDefMD moduleDef = ModuleDefMD.Load(module);
			var int32Type = moduleDef.CorLibTypes.Int32;
			MethodDef methodDef = new MethodDefUser("ValidateInstruction",
				MethodSig.CreateStatic(int32Type, int32Type, int32Type));

			var invokeValidateInstruction = (Func<int, int, int>)validateInstruction.CreateDelegate(typeof(Func<int, int, int>));
#if NETFRAMEWORK
			var ilStreamField = typeof(ILGenerator).GetField("m_ILStream", BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new InvalidOperationException("The code stream was not found");
			byte[] ilBytes = (byte[])ilStreamField?.GetValue(il);
#else
			byte[] ilBytes = GetDynamicMethodIL(validateInstruction);
#endif

			methodDef.Body = dnlib.DotNet.Emit.MethodBodyReader.CreateCilBody(
				moduleDef,
				ilBytes,
				[],
				[new dnlib.DotNet.Parameter(0, int32Type), new dnlib.DotNet.Parameter(1, int32Type)],
				0, 1, (uint)ilBytes.Length, 0);

			var result = invokeValidateInstruction(a, b);
			InstructionEmulator emulator = new InstructionEmulator();
			emulator.Initialize(methodDef, true);
			emulator.SetArg(methodDef.Parameters[0], new Int32Value(a));
			emulator.SetArg(methodDef.Parameters[1], new Int32Value(b));
			var blocks = new Blocks(methodDef);
			emulator.Emulate(blocks.MethodBlocks.GetAllBlocks()[0].Instructions);
			var emulatedValue = (Int32Value)emulator.Pop();
			Assert.AreEqual(result, emulatedValue.Value);
		}

#if !NETFRAMEWORK
		/// <summary>
		/// Retrieves the IL byte array from a DynamicMethod using reflection.
		/// </summary>
		private static byte[] GetDynamicMethodIL(DynamicMethod dm) {
			if (dm == null) throw new ArgumentNullException(nameof(dm));

			// Access the private field m_resolver
			var resolverField = typeof(DynamicMethod)
				.GetField("_resolver", BindingFlags.NonPublic | BindingFlags.Instance);

			if (resolverField == null)
				throw new InvalidOperationException("Could not find _resolver field.");

			object resolver = resolverField.GetValue(dm);
			if (resolver == null)
				throw new InvalidOperationException("DynamicMethod has no resolver, check that delegate was emitted.");

			// Access the private field m_code inside the resolver
			var codeField = resolver.GetType()
				.GetField("m_code", BindingFlags.NonPublic | BindingFlags.Instance);

			if (codeField == null)
				throw new InvalidOperationException("Could not find m_code field.");

			return (byte[])codeField.GetValue(resolver);
		}
#endif
	}
}
