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
using de4dot.blocks;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4 {
	/// <summary>
	/// Inlines non-string generic constants (scalar values and arrays) decrypted by
	/// GenericConstantDecrypter. Handles int, float, double, long, and typed arrays
	/// like int[], float[], byte[], etc.
	///
	/// Extends MethodReturnValueInliner directly (rather than ValueInlinerBase) because
	/// the generic decrypter calls are MethodSpec → MemberRef, which need resolution to
	/// MethodDef for the handler lookup to work.
	/// </summary>
	class GenericConstantInliner : MethodReturnValueInliner {
		readonly MethodDefAndDeclaringTypeDict<Func<MethodDef, MethodSpec, object[], object>> _decrypterMethods = new();
		readonly InitializedDataCreator _initializedDataCreator;

		class MyCallResult : CallResult {
			public readonly MethodDef resolvedMethod;
			public readonly MethodSpec gim;
			public MyCallResult(Block block, int callEndIndex, MethodDef resolvedMethod, MethodSpec gim)
				: base(block, callEndIndex) {
				this.resolvedMethod = resolvedMethod;
				this.gim = gim;
			}
		}

		public override bool HasHandlers => _decrypterMethods.Count != 0;

		public GenericConstantInliner(ModuleDefMD module, InitializedDataCreator initializedDataCreator) {
			_initializedDataCreator = initializedDataCreator;
		}

		public void Add(MethodDef method, Func<MethodDef, MethodSpec, object[], object> handler) {
			if (method is not null)
				_decrypterMethods.Add(method, handler);
		}

		protected override CallResult CreateCallResult(IMethod method, MethodSpec gim, Block block, int callInstrIndex) {
			// Only handle non-string generic instantiations; <string> calls go to the string inliner
			if (gim is null)
				return null;
			if (IsStringInstantiation(gim))
				return null;

			// Resolve MemberRef → MethodDef for the handler lookup
			var handler = _decrypterMethods.Find(method);
			var resolved = method as MethodDef;
			if (handler is null) {
				resolved = (method as IMethodDefOrRef)?.ResolveMethodDef();
				if (resolved is null)
					return null;
				handler = _decrypterMethods.Find(resolved);
				if (handler is null)
					return null;
			}
			if (resolved is null)
				return null;

			return new MyCallResult(block, callInstrIndex, resolved, gim);
		}

		static bool IsStringInstantiation(MethodSpec gim) {
			var gims = gim.GenericInstMethodSig;
			if (gims is null || gims.GenericArguments.Count != 1)
				return false;
			var ga = gims.GenericArguments[0];
			return ga is not null && ga.ElementType == ElementType.String;
		}

		protected override void InlineAllCalls() {
			foreach (var tmp in callResults) {
				var callResult = (MyCallResult)tmp;
				var handler = _decrypterMethods.Find(callResult.resolvedMethod);
				callResult.returnValue = handler(callResult.resolvedMethod, callResult.gim, callResult.args);
			}
		}

		protected override void InlineReturnValues(IList<CallResult> callResults) {
			foreach (var callResult in callResults) {
				var block = callResult.block;
				int num = callResult.callEndIndex - callResult.callStartIndex + 1;

				// Skip string results — those are handled by the string inliner
				if (callResult.returnValue is string)
					continue;

				switch (callResult.returnValue) {
				case ArrayConstant arrayConst:
					InlineArray(block, callResult, num, arrayConst);
					break;
				case int intVal:
					block.Replace(callResult.callStartIndex, num, Instruction.CreateLdcI4(intVal));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic int32: {0}", intVal);
					break;
				case long longVal:
					block.Replace(callResult.callStartIndex, num, OpCodes.Ldc_I8.ToInstruction(longVal));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic int64: {0}", longVal);
					break;
				case float floatVal:
					block.Replace(callResult.callStartIndex, num, OpCodes.Ldc_R4.ToInstruction(floatVal));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic single: {0}", floatVal);
					break;
				case double doubleVal:
					block.Replace(callResult.callStartIndex, num, OpCodes.Ldc_R8.ToInstruction(doubleVal));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic double: {0}", doubleVal);
					break;
				case bool boolVal:
					block.Replace(callResult.callStartIndex, num, Instruction.CreateLdcI4(boolVal ? 1 : 0));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic boolean: {0}", boolVal);
					break;
				case uint uintVal:
					block.Replace(callResult.callStartIndex, num, Instruction.CreateLdcI4((int)uintVal));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic uint32: {0}", uintVal);
					break;
				case ulong ulongVal:
					block.Replace(callResult.callStartIndex, num, OpCodes.Ldc_I8.ToInstruction((long)ulongVal));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic uint64: {0}", ulongVal);
					break;
				case short shortVal:
					block.Replace(callResult.callStartIndex, num, Instruction.CreateLdcI4(shortVal));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic int16: {0}", shortVal);
					break;
				case ushort ushortVal:
					block.Replace(callResult.callStartIndex, num, Instruction.CreateLdcI4(ushortVal));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic uint16: {0}", ushortVal);
					break;
				case byte byteVal:
					block.Replace(callResult.callStartIndex, num, Instruction.CreateLdcI4(byteVal));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic byte: {0}", byteVal);
					break;
				case sbyte sbyteVal:
					block.Replace(callResult.callStartIndex, num, Instruction.CreateLdcI4(sbyteVal));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic sbyte: {0}", sbyteVal);
					break;
				case char charVal:
					block.Replace(callResult.callStartIndex, num, Instruction.CreateLdcI4(charVal));
					RemovePostCallInstructions(block, callResult.callStartIndex + 1);
					Logger.v("Decrypted generic char: {0}", charVal);
					break;
				}
			}
		}

		void InlineArray(Block block, CallResult callResult, int num, ArrayConstant arrayConst) {
			if (arrayConst.ElementType is null) {
				Logger.w("Cannot inline array constant: unknown element type");
				return;
			}

			var elementType = arrayConst.ElementType;
			var elemSize = elementType.ElementType.GetPrimitiveSize();
			if (elemSize == -1) {
				Logger.w("Cannot inline array constant: non-primitive element type {0}", elementType);
				return;
			}

			_initializedDataCreator.AddInitializeArrayCode(block, callResult.callStartIndex, num,
				elementType.ToTypeDefOrRef(), arrayConst.Data);
			RemovePostCallInstructions(block, callResult.callStartIndex + 5); // AddInitializeArrayCode adds 5 instructions
			Logger.v("Decrypted generic array: {0} bytes, element type {1}", arrayConst.Data.Length, elementType);
		}

		/// <summary>
		/// Removes unbox.any and castclass instructions that follow a generic decrypter call,
		/// since we've already inlined the concrete value.
		/// </summary>
		static void RemovePostCallInstructions(Block block, int index) {
			if (index >= block.Instructions.Count)
				return;
			var instr = block.Instructions[index];
			if (instr.OpCode.Code is Code.Unbox_Any or Code.Castclass)
				block.Remove(index, 1);
		}
	}
}
