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
using de4dot.blocks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators {
	// Some obfuscators emit reflection "proxy stubs" declared as *instance* methods whose `this`
	// slot is not actually an instance of the declaring type — it carries an arbitrary receiver
	// that the obfuscated code passed as a weakly-typed (object) argument to a static proxy
	// dispatcher. That is unverifiable but runnable IL, and the obfuscated original hides it
	// because the receiver is only ever passed as `object` to the dispatcher.
	//
	// Once the proxy dispatcher call has been resolved back to the real target (ProxyCallFixer),
	// the lie becomes visible: the stub body is
	//
	//     ldarg.0; ldarg.1; ...; ldarg.N; call instance R Target::M(p1..pN); ret
	//
	// where `this` is used as the receiver of Target::M but the declaring type is unrelated to
	// Target. That fails verification at BOTH the stub (bad receiver for the inner call) and every
	// call site (a Target-typed value pushed where a declaring-type `this` is expected).
	//
	// The repair is to make the stub honest: convert it to a static method whose first parameter is
	// the receiver, typed as the target's declaring type. This needs no IL edits anywhere:
	//   - inside the stub, static arg0 == the old instance `this` slot, so ldarg indices are already
	//     correct;
	//   - at every call site the pushed values and stack depth are unchanged (receiver + N args ==
	//     N+1 static args), and callers reference the MethodDef, so the call is re-emitted against
	//     the new signature automatically.
	//
	// Only provably-invalid methods are touched: the guard requires that the declaring type is NOT
	// assignable to the target's declaring type, so a legitimate stub (e.g. a subclass forwarding to
	// a base-class method) is left completely alone.
	public class FakeInstanceStubFixer {
		readonly ModuleDefMD module;
		readonly List<MethodDef> fixedMethods = new List<MethodDef>();

		public FakeInstanceStubFixer(ModuleDefMD module) => this.module = module;

		public IList<MethodDef> FixedMethods => fixedMethods;

		public int Fix() {
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (TryFix(method))
						fixedMethods.Add(method);
				}
			}
			return fixedMethods.Count;
		}

		bool TryFix(MethodDef method) {
			if (method == null || method.IsStatic || method.Body == null || method.DeclaringType == null)
				return false;
			// Only plain, non-virtual forwarding stubs. A virtual method's signature is part of a
			// vtable contract and must never be rewritten here. Constructors chain to a base ctor
			// with exactly this body shape and are obviously not proxy stubs.
			if (method.IsVirtual || method.IsAbstract || method.HasGenericParameters)
				return false;
			if (method.IsConstructor || method.IsStaticConstructor || method.IsRuntimeSpecialName)
				return false;
			var sig = method.MethodSig;
			if (sig == null || !sig.HasThis || sig.Generic)
				return false;

			var target = GetForwardedCallTarget(method);
			if (target == null)
				return false;

			var targetSig = target.MethodSig;
			if (targetSig == null || !targetSig.HasThis)
				return false;

			var targetDeclType = target.DeclaringType;
			if (targetDeclType == null)
				return false;
			var targetTypeDef = targetDeclType.ResolveTypeDef();
			// A value-type receiver would need a managed pointer, not an object ref — out of scope.
			if (targetTypeDef != null && targetTypeDef.IsValueType)
				return false;

			// THE GUARD: only touch methods that are already provably invalid. If `this` really is
			// assignable to the target's declaring type the IL verifies as-is and must be left alone.
			if (IsAssignableTo(method.DeclaringType, targetDeclType))
				return false;

			var receiverSig = targetDeclType.ToTypeSig();
			if (receiverSig == null)
				return false;

			MakeStatic(method, receiverSig);
			return true;
		}

		// Returns the single instance method this stub forwards to, if its body is exactly
		// `ldarg.0 .. ldarg.N; call/callvirt target; ret` with the args passed straight through in
		// order. Anything else returns null.
		IMethod GetForwardedCallTarget(MethodDef method) {
			var body = method.Body;
			if (body == null || body.HasExceptionHandlers)
				return null;
			var instrs = body.Instructions;
			// N params + this => N+1 ldargs, plus call, plus ret.
			int argCount = method.MethodSig.Params.Count + 1;
			if (instrs.Count != argCount + 2)
				return null;

			for (int i = 0; i < argCount; i++) {
				if (GetLdargIndex(instrs[i], method) != i)
					return null;
			}

			var call = instrs[argCount];
			if (call.OpCode.Code != Code.Call && call.OpCode.Code != Code.Callvirt)
				return null;
			if (instrs[argCount + 1].OpCode.Code != Code.Ret)
				return null;

			var target = call.Operand as IMethod;
			if (target == null || target.MethodSig == null)
				return null;
			// The stub must pass exactly its own parameters through — receiver + N args.
			if (target.MethodSig.Params.Count != method.MethodSig.Params.Count)
				return null;
			return target;
		}

		static int GetLdargIndex(Instruction instr, MethodDef method) {
			switch (instr.OpCode.Code) {
			case Code.Ldarg_0: return 0;
			case Code.Ldarg_1: return 1;
			case Code.Ldarg_2: return 2;
			case Code.Ldarg_3: return 3;
			case Code.Ldarg:
			case Code.Ldarg_S:
				var p = instr.Operand as Parameter;
				return p == null ? -1 : p.Index;
			default:
				return -1;
			}
		}

		// Walks the base/interface chain comparing by full name. Name comparison matters here: the
		// hierarchy usually leaves this module (System.Object, UnityEditor.Editor, ...) and those
		// TypeRefs frequently cannot be resolved to a TypeDef during deobfuscation, but their names
		// are always available -- so each level is name-compared BEFORE we need to resolve anything.
		// Resolution is only needed to keep climbing; when it fails we stop, having already compared
		// every level we could see.
		static bool IsAssignableTo(TypeDef type, ITypeDefOrRef targetType) {
			if (type == null || targetType == null)
				return false;
			string targetName = targetType.FullName;

			for (var current = type; current != null;) {
				if (current.FullName == targetName)
					return true;
				foreach (var iface in current.Interfaces) {
					if (iface.Interface != null && iface.Interface.FullName == targetName)
						return true;
				}
				var baseType = current.BaseType;
				if (baseType == null)
					break;
				if (baseType.FullName == targetName)
					return true;
				var baseTypeDef = baseType.ResolveTypeDef();
				if (baseTypeDef == null)
					break;
				current = baseTypeDef;
			}
			return false;
		}

		// Converts an instance method to a static one whose first parameter is the former `this`.
		// Existing ldarg indices stay valid because static arg0 occupies the old `this` slot.
		static void MakeStatic(MethodDef method, TypeSig receiverSig) {
			foreach (var pd in method.ParamDefs) {
				if (pd.Sequence >= 1)
					pd.Sequence++;
			}
			method.MethodSig.HasThis = false;
			method.MethodSig.Params.Insert(0, receiverSig);
			method.Attributes |= MethodAttributes.Static;
			method.Parameters.UpdateParameterTypes();
		}
	}
}
