using System.Reflection;
using System.Reflection.Emit;
using de4dot.blocks.cflow;
using dnlib.DotNet;

namespace de4dot.blocks.tests {
	[TestClass]
	public sealed class InstructionEmulatorTest {
		[TestMethod]
		[DynamicData(nameof(OperationsSmokeTest))]
		public void ValidateInstructionsEmulation(string opCodeName, int a, int b) {
			var opCode = (OpCode)typeof(OpCodes).GetField(opCodeName, BindingFlags.Public | BindingFlags.Static)
				?.GetValue(null)!;
			InstructionVerification.ValidateOpCode(a, b, opCode);
		}

		public static IEnumerable<ValueTuple<string, int, int>> OperationsSmokeTest =>
			[
				(nameof(OpCodes.Add), 1, 2),
				(nameof(OpCodes.Add_Ovf_Un), int.MaxValue, int.MaxValue),

				// Shifts, both operands known. Only counts inside 0..31 appear here: outside that
				// range the instruction is undefined in CIL, so the runtime is not ground truth —
				// x64 masks the count to five bits, and matching that would be matching one
				// implementation rather than the spec. Int32ValueOperationsTest asserts the
				// out-of-range counts stay unknown instead.
				(nameof(OpCodes.Shl), 1, 0),
				(nameof(OpCodes.Shl), 1, 31),
				(nameof(OpCodes.Shl), -1, 4),
				(nameof(OpCodes.Shr), int.MinValue, 4),
				(nameof(OpCodes.Shr), -1, 31),
				(nameof(OpCodes.Shr_Un), int.MinValue, 4),
				(nameof(OpCodes.Shr_Un), -1, 31),

				// This is value outside of specification. But .NET, .NET FW and Mono works like that.
				// No need to be spec compliant. If some implementation would be different, let's handle that later
				(nameof(OpCodes.Shr), -1, 33),

				// Unsigned remainder, including the power-of-two divisors that are folded to a mask
				// and the high-bit divisor that is only a power of two when read unsigned.
				(nameof(OpCodes.Rem_Un), 100, 16),
				(nameof(OpCodes.Rem_Un), 37, 24),
				(nameof(OpCodes.Rem_Un), -1, 16),
				(nameof(OpCodes.Rem_Un), -1, unchecked((int)0x80000000u)),
			];
	}
}
