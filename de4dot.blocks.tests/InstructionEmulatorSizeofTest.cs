using System.Reflection.Emit;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using OpCodes = dnlib.DotNet.Emit.OpCodes;

namespace de4dot.blocks.tests {
	/// <summary>
	/// The emulator's <c>sizeof</c> folding.
	///
	/// Obfuscators use <c>sizeof</c> as an opaque constant source, so folding it turns dead branches
	/// into statically decidable ones. The risk runs the other way too: a constant the emulator
	/// invents is indistinguishable downstream from one the program really computes, and
	/// <c>SwitchCflowDeobfuscator</c> will pick a switch arm from it. So every folded type is checked
	/// against what the runtime actually pushes for the same instruction, and every size the target
	/// runtime gets to decide has to stay unknown.
	/// </summary>
	[TestClass]
	public sealed class InstructionEmulatorSizeofTest {
		/// <summary>Emulates <c>sizeof T</c> on its own and returns what was pushed.</summary>
		static Value EmulateSizeof(ITypeDefOrRef type) {
			var module = new ModuleDefUser("test", Guid.NewGuid(),
				new AssemblyRefUser(new AssemblyNameInfo("mscorlib")));
			var declaringType = new TypeDefUser("", "C", module.CorLibTypes.Object.TypeDefOrRef);
			module.Types.Add(declaringType);

			var method = new MethodDefUser("M", MethodSig.CreateStatic(module.CorLibTypes.Int32),
				MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
			declaringType.Methods.Add(method);
			method.Parameters.UpdateParameterTypes();

			var emulator = new InstructionEmulator();
			emulator.Initialize(method, true);
			emulator.Emulate(OpCodes.Sizeof.ToInstruction(type));
			return emulator.Pop();
		}

		static Value EmulateSizeofCorLib(string @namespace, string name) {
			var module = new ModuleDefUser("ref", Guid.NewGuid(),
				new AssemblyRefUser(new AssemblyNameInfo("mscorlib")));
			return EmulateSizeof(new TypeRefUser(module, @namespace, name, module.CorLibTypes.AssemblyRef));
		}

		/// <summary>What the runtime itself pushes for <c>sizeof T</c>, as ground truth.</summary>
		static int RuntimeSizeof(Type type) {
			var method = new DynamicMethod("Sizeof", typeof(int), Type.EmptyTypes,
				typeof(InstructionEmulatorSizeofTest).Module);
			var il = method.GetILGenerator();
			il.Emit(System.Reflection.Emit.OpCodes.Sizeof, type);
			il.Emit(System.Reflection.Emit.OpCodes.Ret);
			return ((Func<int>)method.CreateDelegate(typeof(Func<int>)))();
		}

		static void AssertKnown(int expected, Value value) {
			Assert.IsInstanceOfType<Int32Value>(value, $"expected the known constant {expected}, got {value}");
			var int32 = (Int32Value)value;
			Assert.IsTrue(int32.AllBitsValid(), $"expected the known constant {expected}, got {value}");
			Assert.AreEqual(expected, int32.Value);
		}

		static void AssertUnknown(Value value) =>
			Assert.IsFalse(value is Int32Value int32 && int32.AllBitsValid(),
				$"expected an unknown value, got the constant {value} - the emulator is claiming to know " +
				"a size the target runtime decides");

		/// <summary>
		/// Every folded type against the real instruction, so the table cannot drift from the runtime.
		/// </summary>
		[TestMethod]
		[DynamicData(nameof(FoldedTypes))]
		public void FoldedSizeMatchesTheRuntime(Type type) =>
			AssertKnown(RuntimeSizeof(type), EmulateSizeofCorLib(type.Namespace!, type.Name));

		public static IEnumerable<object[]> FoldedTypes =>
			[
				[typeof(bool)],
				[typeof(sbyte)],
				[typeof(byte)],
				[typeof(char)],
				[typeof(short)],
				[typeof(ushort)],
				[typeof(int)],
				[typeof(uint)],
				[typeof(long)],
				[typeof(ulong)],
				[typeof(float)],
				[typeof(double)],
				[typeof(Guid)],
			];

		/// <summary>
		/// Pointer-sized types depend on the bitness the obfuscated assembly runs under, not on the
		/// bitness of the process running de4dot; Decimal and DateTime have no spec-fixed layout.
		/// </summary>
		[TestMethod]
		[DataRow("System", "IntPtr")]
		[DataRow("System", "UIntPtr")]
		[DataRow("System", "String")]
		[DataRow("System", "Object")]
		[DataRow("System", "Decimal")]
		[DataRow("System", "DateTime")]
		public void SizesTheTargetRuntimeDecidesStayUnknown(string @namespace, string name) =>
			AssertUnknown(EmulateSizeofCorLib(@namespace, name));

		[TestMethod]
		public void ATypeDefinedInTheModuleUnderAnalysisStaysUnknown() {
			var module = new ModuleDefUser("test", Guid.NewGuid(),
				new AssemblyRefUser(new AssemblyNameInfo("mscorlib")));
			var type = new TypeDefUser("", "SomeStruct", module.CorLibTypes.Object.TypeDefOrRef);
			module.Types.Add(type);
			AssertUnknown(EmulateSizeof(type));
		}

		/// <summary>
		/// Matching is on the full name, so a type that merely borrows a corlib name is not mistaken
		/// for the real one.
		/// </summary>
		[TestMethod]
		public void AnUnrelatedTypeNamedInt32StaysUnknown() =>
			AssertUnknown(EmulateSizeofCorLib("SomeNamespace", "Int32"));

		/// <summary>
		/// <c>sizeof</c> pushes one value and pops none. Getting that wrong desynchronises every stack
		/// slot after it, which is a worse failure than an imprecise size.
		/// </summary>
		[TestMethod]
		public void SizeofOnlyPushes() {
			var module = new ModuleDefUser("test", Guid.NewGuid(),
				new AssemblyRefUser(new AssemblyNameInfo("mscorlib")));
			var declaringType = new TypeDefUser("", "C", module.CorLibTypes.Object.TypeDefOrRef);
			module.Types.Add(declaringType);
			var method = new MethodDefUser("M", MethodSig.CreateStatic(module.CorLibTypes.Int32),
				MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
			declaringType.Methods.Add(method);
			method.Parameters.UpdateParameterTypes();

			var emulator = new InstructionEmulator();
			emulator.Initialize(method, true);
			emulator.Push(new Int32Value(0x1234));
			emulator.Emulate(OpCodes.Sizeof.ToInstruction(
				new TypeRefUser(module, "System", "Int32", module.CorLibTypes.AssemblyRef)));
			AssertKnown(sizeof(int), emulator.Pop());
			AssertKnown(0x1234, emulator.Pop());
		}
	}
}
