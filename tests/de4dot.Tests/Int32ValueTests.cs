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

using de4dot.blocks.cflow;
using Xunit;

namespace de4dot.tests {
	/// <summary>
	///     The int32 abstract value lattice.
	///
	///     These are pure static functions, so they are the cheapest thing in the tree to pin — and
	///     the most valuable, because a wrong answer here is invisible until it has already chosen a
	///     branch. The shift guards in particular defend against a regression this code has shipped
	///     before: a shift count that is a nonzero multiple of the operand width computed an
	///     all-bits-valid mask and turned an unknown operand into a known constant 0. Obfuscators emit
	///     oversized shift counts deliberately, so these are live inputs, not hypotheticals.
	/// </summary>
	public class Int32ValueTests {
		static Int32Value Known(int value) => new Int32Value(value);
		static Int32Value Unknown() => Int32Value.CreateUnknown();

		static void AssertKnown(int expected, Int32Value actual) {
			Assert.True(actual.AllBitsValid(), $"expected the known constant {expected}, got {actual}");
			Assert.Equal(expected, actual.Value);
		}

		static void AssertUnknown(Int32Value actual) =>
			Assert.False(actual.AllBitsValid(),
				$"expected an unknown value, got the constant {actual.Value}");

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(31)]
		public void ShiftsInRangeAreComputed(int count) {
			AssertKnown(1 << count, Int32Value.Shl(Known(1), Known(count)));
			AssertKnown(unchecked((int)(0x80000000u >> count)), Int32Value.Shr_Un(Known(unchecked((int)0x80000000u)), Known(count)));
		}

		/// <summary>
		///     A count of 32 is a no-op in C# (the count is masked) but undefined in CIL. Computing a
		///     mask from `32 - count` yields a shift by zero, which reads as "every bit known" — the
		///     exact shape of the regression this guard exists for.
		/// </summary>
		[Theory]
		[InlineData(32)]
		[InlineData(64)]
		[InlineData(-1)]
		[InlineData(int.MinValue)]
		public void ShiftsOutOfRangeAreUnknown(int count) {
			AssertUnknown(Int32Value.Shl(Unknown(), Known(count)));
			AssertUnknown(Int32Value.Shr(Unknown(), Known(count)));
			AssertUnknown(Int32Value.Shr_Un(Unknown(), Known(count)));
			// Also unknown when the operand IS known: the operation itself is undefined.
			AssertUnknown(Int32Value.Shl(Known(1), Known(count)));
		}

		[Fact]
		public void ShiftByAnUnknownCountIsUnknown() {
			AssertUnknown(Int32Value.Shl(Known(1), Unknown()));
			AssertUnknown(Int32Value.Shr(Known(1), Unknown()));
			AssertUnknown(Int32Value.Shr_Un(Known(1), Unknown()));
		}

		[Fact]
		public void ShiftByZeroPreservesPartialKnowledge() {
			var partial = Int32Value.Shl(Unknown(), Known(4));
			Assert.Same(partial, Int32Value.Shl(partial, Known(0)));
		}

		/// <summary>
		///     `(uint)a % (uint)b == (uint)a &amp; (uint)(b - 1)` when b is a power of two, which lets a
		///     partially-known operand keep the bits the mask preserves instead of collapsing to
		///     unknown. Only valid unsigned, and only in the Rem_Un overload.
		/// </summary>
		[Theory]
		[InlineData(1)]
		[InlineData(2)]
		[InlineData(16)]
		[InlineData(256)]
		public void UnsignedRemainderByAPowerOfTwoIsComputedFromKnownOperands(int divisor) {
			for (int value = 0; value < 40; value++)
				AssertKnown(value % divisor, Int32Value.Rem_Un(Known(value), Known(divisor)));
		}

		[Fact]
		public void UnsignedRemainderByAPowerOfTwoNarrowsAPartiallyKnownOperand() {
			// (unknown << 8) has its low 8 bits known to be zero, so % 16 is known to be 0.
			var lowBitsZero = Int32Value.Shl(Unknown(), Known(8));
			AssertKnown(0, Int32Value.Rem_Un(lowBitsZero, Known(16)));
		}

		[Fact]
		public void UnsignedRemainderByANonPowerOfTwoStaysUnknown() =>
			AssertUnknown(Int32Value.Rem_Un(Int32Value.Shl(Unknown(), Known(8)), Known(24)));

		[Fact]
		public void UnsignedRemainderByAnUnknownDivisorStaysUnknown() =>
			AssertUnknown(Int32Value.Rem_Un(Known(100), Unknown()));

		/// <summary>
		///     0x80000000 is a power of two when read unsigned. Reading the divisor as signed would
		///     make it negative and skip the fold, so this pins the unsigned interpretation.
		/// </summary>
		[Fact]
		public void UnsignedRemainderTreatsTheHighBitDivisorAsAPowerOfTwo() {
			int divisor = unchecked((int)0x80000000u);
			AssertKnown(unchecked((int)0x7FFFFFFFu), Int32Value.Rem_Un(Known(unchecked((int)0xFFFFFFFFu)), Known(divisor)));
			AssertKnown(3, Int32Value.Rem_Un(Known(3), Known(divisor)));
		}
	}

	/// <summary>The int64 lattice. Same guards, 64-bit widths, shift counts are still Int32Value.</summary>
	public class Int64ValueTests {
		static Int64Value Known(long value) => new Int64Value(value);
		static Int32Value Count(int value) => new Int32Value(value);

		static void AssertKnown(long expected, Int64Value actual) {
			Assert.True(actual.AllBitsValid(), $"expected the known constant {expected}, got {actual}");
			Assert.Equal(expected, actual.Value);
		}

		static void AssertUnknown(Int64Value actual) =>
			Assert.False(actual.AllBitsValid(), $"expected an unknown value, got {actual.Value}");

		[Theory]
		[InlineData(0)]
		[InlineData(1)]
		[InlineData(63)]
		public void ShiftsInRangeAreComputed(int count) =>
			AssertKnown(1L << count, Int64Value.Shl(Known(1), Count(count)));

		[Theory]
		[InlineData(64)]
		[InlineData(128)]
		[InlineData(-1)]
		[InlineData(int.MinValue)]
		public void ShiftsOutOfRangeAreUnknown(int count) {
			AssertUnknown(Int64Value.Shl(Int64Value.CreateUnknown(), Count(count)));
			AssertUnknown(Int64Value.Shr(Int64Value.CreateUnknown(), Count(count)));
			AssertUnknown(Int64Value.Shr_Un(Int64Value.CreateUnknown(), Count(count)));
			AssertUnknown(Int64Value.Shl(Known(1), Count(count)));
		}

		/// <summary>A 32 count is in range for int64 and must still be computed, not rejected.</summary>
		[Fact]
		public void AThirtyTwoCountIsInRangeForInt64() => AssertKnown(1L << 32, Int64Value.Shl(Known(1), Count(32)));
	}
}
