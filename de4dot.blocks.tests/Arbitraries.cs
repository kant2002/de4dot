using System.Reflection.Emit;
using FsCheck;
using FsCheck.Fluent;

namespace de4dot.blocks.tests {
	internal class Arbitraries {
		public static Arbitrary<OpCode> ValidBinaryOperators() =>
			Arb.From(
				Gen.Elements(
					[OpCodes.Add, OpCodes.Sub, OpCodes.Mul, 
					// Require modelling of the exceptions as values
					// OpCodes.Add_Ovf_Un, 
					OpCodes.Shr, OpCodes.Shl, OpCodes.Rem_Un]));
		public static Arbitrary<(OpCode, (int, int))> ValidBinaryInt32Expressions() =>
			Arb.Zip(
				ValidBinaryOperators(),
				Arb.Zip(
					Arb.From(ArbMap.Default.GeneratorFor<int>()),
					Arb.From(ArbMap.Default.GeneratorFor<int>())))
			.Filter<(OpCode, (int, int))>((expr) => !(
				(expr.Item1.Name == OpCodes.Rem_Un.Name && expr.Item2.Item2 == 0)
				));
	}
}
