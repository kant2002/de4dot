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
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.code.AssemblyClient;
using de4dot.blocks;

namespace de4dot.code {
	public abstract class StringInlinerBase : MethodReturnValueInliner {
		/// <summary>
		///     Is this a generic instantiation over exactly <c>System.String</c>, i.e.
		///     <c>M&lt;string&gt;</c>?
		///
		///     A decrypter declared as <c>!!0 M&lt;T&gt;(int32)</c> is called through a MethodSpec, and
		///     only the instantiation over string returns a string. Inlining any other instantiation
		///     as an <c>ldstr</c> would put a string where the call site expects something else.
		/// </summary>
		protected static bool IsGenericStringInstantiation(MethodSpec gim) {
			var gims = gim?.GenericInstMethodSig;
			if (gims is null || gims.GenericArguments.Count != 1)
				return false;
			var ga = gims.GenericArguments[0];
			return ga is { ElementType: ElementType.String };
		}

		protected override void InlineReturnValues(IList<CallResult> callResults) {
			foreach (var callResult in callResults) {
				var block = callResult.block;
				int num = callResult.callEndIndex - callResult.callStartIndex + 1;

				var decryptedString = callResult.returnValue as string;
				if (decryptedString == null)
					continue;

				int ldstrIndex = callResult.callStartIndex;
				block.Replace(ldstrIndex, num, OpCodes.Ldstr.ToInstruction(decryptedString));

				// A decrypter returning !!0 rather than string is followed by a castclass to
				// System.String at the call site. The ldstr just inlined is already a string, so the
				// cast has nothing left to do.
				if (ldstrIndex + 1 < block.Instructions.Count) {
					var instr = block.Instructions[ldstrIndex + 1];
					if (instr.OpCode.Code == Code.Castclass && instr.Operand.ToString() == "System.String")
						block.Remove(ldstrIndex + 1, 1);
				}

				// Some decrypters hand their result to String.Intern to deduplicate at runtime. An
				// ldstr operand is already interned by the runtime, so the call is redundant.
				if (ldstrIndex + 1 < block.Instructions.Count) {
					var instr = block.Instructions[ldstrIndex + 1];
					if (instr.OpCode.Code == Code.Call) {
						if (instr.Operand is IMethod calledMethod &&
							calledMethod.FullName == "System.String System.String::Intern(System.String)") {
							block.Remove(ldstrIndex + 1, 1);
						}
					}
				}

				Logger.v("Decrypted string: {0}", Utils.ToCsharpString(decryptedString));
			}
		}
	}

	public class DynamicStringInliner : StringInlinerBase {
		IAssemblyClient assemblyClient;
		Dictionary<int, int> methodTokenToId = new Dictionary<int, int>();

		class MyCallResult : CallResult {
			public int methodId;
			public MethodSpec gim;
			public MyCallResult(Block block, int callEndIndex, int methodId, MethodSpec gim)
				: base(block, callEndIndex) {
				this.methodId = methodId;
				this.gim = gim;
			}
		}

		public override bool HasHandlers => methodTokenToId.Count != 0;

		public DynamicStringInliner(IAssemblyClient assemblyClient) => this.assemblyClient = assemblyClient;

		/// <summary>
		///     Kept so existing callers still compile and bind. <c>DynamicStringInliner</c> is public
		///     API, and the richer overload conveys nothing this one does not until
		///     <see cref="StringDecrypterMethodInfo"/> carries more than a token.
		/// </summary>
		public void Initialize(IEnumerable<int> methodTokens) =>
			Initialize(methodTokens.Select(token => new StringDecrypterMethodInfo(token)));

		public void Initialize(IEnumerable<StringDecrypterMethodInfo> methodInfos) {
			methodTokenToId.Clear();
			foreach (var info in methodInfos) {
				if (methodTokenToId.ContainsKey(info.MethodToken))
					continue;
				int methodId = assemblyClient.StringDecrypterService.DefineStringDecrypter(info.MethodToken);
				methodTokenToId[info.MethodToken] = methodId;
			}
		}

		protected override CallResult CreateCallResult(IMethod method, MethodSpec gim, Block block, int callInstrIndex) {
			if (gim is not null && !IsGenericStringInstantiation(gim))
				return null;
			if (!TryGetMethodId(method, out int methodId))
				return null;
			return new MyCallResult(block, callInstrIndex, methodId, gim);
		}

		bool TryGetMethodId(IMethod method, out int methodId) {
			methodId = 0;
			int methodToken = method.MDToken.ToInt32();
			if (methodTokenToId.TryGetValue(methodToken, out methodId))
				return true;
			var resolved = (method as IMethodDefOrRef)?.ResolveMethodDef();
			if (resolved is null)
				return false;
			methodToken = resolved.MDToken.ToInt32();
			return methodTokenToId.TryGetValue(methodToken, out methodId);
		}

		protected override void InlineAllCalls() {
			var sortedCalls = new Dictionary<int, List<MyCallResult>>();
			foreach (var tmp in callResults) {
				var callResult = (MyCallResult)tmp;
				if (!sortedCalls.TryGetValue(callResult.methodId, out var list))
					sortedCalls[callResult.methodId] = list = new List<MyCallResult>(callResults.Count);
				list.Add(callResult);
			}

			foreach (var methodId in sortedCalls.Keys) {
				var list = sortedCalls[methodId];
				var args = new object[list.Count];
				for (int i = 0; i < list.Count; i++) {
					AssemblyData.SimpleData.Pack(list[i].args);
					args[i] = list[i].args;
				}
				var decryptedStrings = assemblyClient.StringDecrypterService.DecryptStrings(methodId, args, Method.MDToken.ToInt32());
				if (decryptedStrings.Length != args.Length)
					throw new ApplicationException("Invalid decrypted strings array length");
				AssemblyData.SimpleData.Unpack(decryptedStrings);
				for (int i = 0; i < list.Count; i++)
					list[i].returnValue = (string)decryptedStrings[i];
			}
		}
	}

	public class StaticStringInliner : StringInlinerBase {
		MethodDefAndDeclaringTypeDict<Func<MethodDef, MethodSpec, object[], string>> stringDecrypters = new MethodDefAndDeclaringTypeDict<Func<MethodDef, MethodSpec, object[], string>>();

		public override bool HasHandlers => stringDecrypters.Count != 0;
		public IEnumerable<MethodDef> Methods => stringDecrypters.GetKeys();

		class MyCallResult : CallResult {
			public IMethod IMethod;
			public MethodSpec gim;
			public MyCallResult(Block block, int callEndIndex, IMethod method, MethodSpec gim)
				: base(block, callEndIndex) {
				IMethod = method;
				this.gim = gim;
			}
		}

		public void Add(MethodDef method, Func<MethodDef, MethodSpec, object[], string> handler) {
			if (method != null)
				stringDecrypters.Add(method, handler);
		}

		protected override void InlineAllCalls() {
			foreach (var tmp in callResults) {
				var callResult = (MyCallResult)tmp;
				var handler = stringDecrypters.Find(callResult.IMethod);
				callResult.returnValue = handler((MethodDef)callResult.IMethod, callResult.gim, callResult.args);
			}
		}

		protected override CallResult CreateCallResult(IMethod method, MethodSpec gim, Block block, int callInstrIndex) {
			if (gim is not null && !IsGenericStringInstantiation(gim))
				return null;
			var handler = stringDecrypters.Find(method);
			if (handler is null) {
				var byDef = (method as IMethodDefOrRef)?.ResolveMethodDef();
				if (byDef is null)
					return null;
				handler = stringDecrypters.Find(byDef);
				if (handler is null)
					return null;
			}

			// InlineAllCalls casts this to MethodDef unconditionally, and Find also matches a
			// MemberRef directly (on declaring-type name + name + signature) -- so normalising only
			// on the lookup-failed path leaves the successful-MemberRef path to throw
			// InvalidCastException later, which surfaces as "Could not deobfuscate method" and
			// silently leaves the method encrypted. Normalise whichever way we got here.
			if (method is not MethodDef) {
				var resolved = (method as IMethodDefOrRef)?.ResolveMethodDef();
				if (resolved is null)
					return null;
				method = resolved;
			}
			return new MyCallResult(block, callInstrIndex, method, gim);
		}
	}
}
