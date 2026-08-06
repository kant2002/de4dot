using System.Reflection.Emit;
using AD.FsCheck.MSTest;

namespace de4dot.blocks.tests {
	[Properties(MaxTest = 1_000)]
	public sealed class EmulationProperties {
		[Property(Arbitrary = [typeof(Arbitraries)])]
		public void SingleInstructionMatchRuntime((OpCode, (int, int)) expr) => InstructionVerification.ValidateOpCode(expr.Item2.Item1, expr.Item2.Item2, expr.Item1);

	}
}
