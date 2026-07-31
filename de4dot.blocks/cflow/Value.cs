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

using System.Collections.Generic;

namespace de4dot.blocks.cflow {
	public enum ValueType : byte {
		Unknown,
		Null,
		Object,
		Boxed,
		Int32,
		Int64,
		Real8,
		String,
	}

	public enum Bool3 {
		Unknown = -1,
		False,
		True,
	}

	public abstract class Value {
		public readonly ValueType valueType;
		public bool IsUnknown() => valueType == ValueType.Unknown;
		public bool IsNull() => valueType == ValueType.Null;
		public bool IsObject() => valueType == ValueType.Object;
		public bool IsBoxed() => valueType == ValueType.Boxed;
		public bool IsInt32() => valueType == ValueType.Int32;
		public bool IsInt64() => valueType == ValueType.Int64;
		public bool IsReal8() => valueType == ValueType.Real8;
		public bool IsString() => valueType == ValueType.String;
		protected Value(ValueType valueType) => this.valueType = valueType;
	}

	public class UnknownValue : Value {
		public UnknownValue() : base(ValueType.Unknown) { }
		public override string ToString() => "<unknown>";
	}

	public class ObjectValue : Value {
		public readonly object? obj;	// can be null but that doesn't mean that this ObjectValue instance is null
		public ObjectValue() : this(null) { }
		public ObjectValue(object? obj) : base(ValueType.Object) => this.obj = obj;
		protected ObjectValue(object? obj, ValueType vt) : base(vt) => this.obj = obj;
		public override string ToString() => "<non-null object>";
	}

	/// <summary>
	///     An int32 array created by <c>newarr</c> whose element values are being tracked, so that
	///     <c>stelem</c>/<c>ldelem</c> can round-trip a constant through it.
	///
	///     It reports <see cref="ValueType.Unknown"/> rather than <see cref="ValueType.Object"/> on
	///     purpose: an <c>ObjectValue</c> is treated as provably non-null, which would let
	///     <c>brfalse</c>/<c>brtrue</c> resolve a branch on the array reference. Tracking elements
	///     is not meant to buy that, so the reference itself stays opaque.
	///
	///     Every slot holds an <see cref="Int32Value"/> for as long as the value exists. The
	///     emulator maintains that by refusing to track non-int32 arrays and by blanking every slot
	///     when it meets a store it cannot place — see <c>InstructionEmulator.Emulate_Stelem</c>.
	/// </summary>
	public class TrackedArrayValue : ObjectValue {
		bool escaped;

		public TrackedArrayValue(List<Value> arr)
			: base(arr, ValueType.Unknown) { }

		/// <summary>The backing element list. Mutated in place, and aliased by every copy of this
		/// value on the stack or in a local — which is what makes <c>dup</c> behave correctly.</summary>
		public List<Value> Elements => (List<Value>)obj!;

		/// <summary>
		///     The reference reached code the emulator does not execute, so nothing about the
		///     elements can be relied on again.
		/// </summary>
		public bool HasEscaped => escaped;

		/// <summary>
		///     Give up on this array permanently.
		///
		///     Sticky on purpose. Blanking the elements without latching would let a later modelled
		///     <c>stelem</c> re-establish a "known" element, which is wrong once the reference is
		///     somewhere the emulator cannot see: any subsequent call could reach it through the
		///     alias that escaped, without ever appearing to touch this value.
		///
		///     The element list is shared with every alias, so blanking it here is observed through
		///     the copies held in locals and elsewhere on the stack.
		/// </summary>
		public void Escape() {
			escaped = true;
			var elements = Elements;
			for (int i = 0; i < elements.Count; i++)
				elements[i] = Int32Value.CreateUnknown();
		}

		public override string ToString() => escaped ? "<escaped array>" : "<tracked array>";
	}

	public class NullValue : Value {
		// There's only one type of null
		public static readonly NullValue Instance = new NullValue();
		NullValue() : base(ValueType.Null) { }
		public override string ToString() => "null";
	}

	public class BoxedValue : Value {
		public readonly Value value;
		public BoxedValue(Value value) : base(ValueType.Boxed) => this.value = value;
		public override string ToString() => $"box({value.ToString()})";
	}

	public class StringValue : Value {
		public readonly string value;
		public StringValue(string value) : base(ValueType.String) => this.value = value;
		public override string ToString() => $"\"{value}\"";
	}
}
