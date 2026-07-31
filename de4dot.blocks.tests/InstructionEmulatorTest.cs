using System.Reflection;
using System.Reflection.Emit;
using de4dot.blocks.cflow;
using dnlib.DotNet;

namespace de4dot.blocks.tests {
	[TestClass]
	public sealed class InstructionEmulatorTest {
		[TestMethod]
		[DynamicData(nameof(OperationsSmokeTest))]
		public void ValidateInstructionsEmulation(OpCode opCode, int a, int b) {
			InstructionVerification.ValidateOpCode(a, b, opCode);
		}

		public static IEnumerable<object[]> OperationsSmokeTest =>
			[
				[OpCodes.Add, 1, 2],
				[OpCodes.Add_Ovf_Un, int.MaxValue, int.MaxValue],
			];
	}
}
