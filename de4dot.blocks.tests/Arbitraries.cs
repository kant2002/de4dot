using System.Reflection.Emit;
using FsCheck;
using FsCheck.Fluent;

namespace de4dot.blocks.tests {
	internal class Arbitraries {
		public static Arbitrary<OpCode> BinaryOperators() => Arb.From(
			Gen.Elements(
				[OpCodes.Add, OpCodes.Sub, OpCodes.Mul]));
	}
}
