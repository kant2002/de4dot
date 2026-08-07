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
		readonly HashSet<MethodDef> delegateTargets = new HashSet<MethodDef>();

		public FakeInstanceStubFixer(ModuleDefMD module) => this.module = module;

		public IList<MethodDef> FixedMethods => fixedMethods;

		public int Fix() {
			FindDelegateTargets();
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (TryFix(method))
						fixedMethods.Add(method);
				}
			}
			return fixedMethods.Count;
		}

		/// <summary>
		///     Every method whose address is taken to build a delegate. Making one of those static
		///     changes what the delegate is bound to and what reflection reports about it, so they are
		///     excluded regardless of how invalid the stub looks.
		/// </summary>
		void FindDelegateTargets() {
			delegateTargets.Clear();
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					var body = method.Body;
					if (body == null)
						continue;
					foreach (var instr in body.Instructions) {
						if (instr.OpCode.Code != Code.Ldftn && instr.OpCode.Code != Code.Ldvirtftn)
							continue;
						if ((instr.Operand as IMethodDefOrRef)?.ResolveMethodDef() is MethodDef target)
							delegateTargets.Add(target);
					}
				}
			}
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
			// A property or event accessor cannot go static while its PropertyDef/EventDef still
			// declares an instance signature -- the result is metadata no loader accepts.
			if (method.SemanticsAttributes != 0)
				return false;
			// Changing the signature of anything visible outside the assembly breaks every external
			// consumer, and de4dot processes several files in one run.
			if (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly)
				return false;
			// A stub used to build a delegate would become a static method closed over its first
			// argument: still runnable, no longer what the delegate type or reflection expects.
			if (delegateTargets.Contains(method))
				return false;
			var sig = method.MethodSig;
			if (sig == null || !sig.HasThis || sig.Generic)
				return false;
			// The stub's own declaring type being generic means call sites reach it through MemberRefs
			// with their own signatures, which nothing here rewrites -- the definition would move and
			// the call sites would keep asking for the old shape.
			if (method.DeclaringType.HasGenericParameters)
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
			// An unresolvable target is not evidence that it is a class: ToTypeSig() would happily
			// emit a ClassSig for a struct, so a type we cannot inspect is one we do not touch.
			if (targetTypeDef == null || targetTypeDef.IsValueType)
				return false;

			// THE GUARD: only touch methods that are already provably invalid. If `this` really is
			// assignable to the target's declaring type the IL verifies as-is and must be left alone
			// — and so must anything the walk could not decide, because "I could not follow the base
			// chain" is not evidence of a broken method. Only a definite `false` allows the rewrite.
			if (IsAssignableTo(method.DeclaringType, targetDeclType) != false)
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

		// Walks the base and interface chain comparing by full name. Name comparison matters here:
		// the hierarchy usually leaves this module (System.Object and whatever framework or
		// third-party base the type derives from) and those TypeRefs frequently cannot be resolved to
		// a TypeDef while deobfuscating, but their names are always available -- so each level is
		// name-compared BEFORE anything needs resolving.
		//
		// Returns null for "cannot tell", which is NOT the same as false. The caller rewrites a
		// method only when this is definitely false, so an unresolvable base type, a generic base
		// whose arguments this cannot substitute, or a recursion guard tripping must all report
		// "unknown" and leave the method alone. Reporting false in those cases is what makes an
		// ordinary forwarding method look provably invalid.
		static bool? IsAssignableTo(TypeDef type, ITypeDefOrRef targetType) {
			if (type == null || targetType == null)
				return null;
			string targetName = targetType.FullName;
			// A type whose base chain names itself is readable metadata and would spin here forever.
			// de4dot's input is hostile by definition, so the guard is not optional.
			var visited = new HashSet<TypeDef>();

			for (var current = type; current != null;) {
				if (!visited.Add(current))
					return null;
				if (current.FullName == targetName)
					return true;

				switch (InterfacesInclude(current, targetName, visited)) {
				case true: return true;
				case null: return null;
				}

				var baseType = current.BaseType;
				if (baseType == null)
					return false;			// reached System.Object without a match: genuinely not assignable
				if (baseType.FullName == targetName)
					return true;
				// A generic base carries its arguments in the FullName, so `NS.B`1<System.Int32>` never
				// name-matches the `NS.B`1<!0>` seen while climbing. Substituting them is out of scope
				// here, so a generic base means the answer is not knowable by name alone.
				if (baseType.NumberOfGenericParameters > 0 || baseType.FullName.IndexOf('<') >= 0)
					return null;
				var baseTypeDef = baseType.ResolveTypeDef();
				if (baseTypeDef == null)
					return null;		// cannot see the rest of the chain, so cannot rule the target out
				current = baseTypeDef;
			}
			return false;
		}

		/// <summary>
		///     Does <paramref name="type"/> implement <paramref name="targetName"/>, directly or
		///     through an interface that inherits it? Null when an interface will not resolve.
		/// </summary>
		static bool? InterfacesInclude(TypeDef type, string targetName, HashSet<TypeDef> visited) {
			foreach (var iface in type.Interfaces) {
				var ifaceRef = iface.Interface;
				if (ifaceRef == null)
					continue;
				if (ifaceRef.FullName == targetName)
					return true;
				if (ifaceRef.NumberOfGenericParameters > 0 || ifaceRef.FullName.IndexOf('<') >= 0)
					return null;
				var ifaceDef = ifaceRef.ResolveTypeDef();
				if (ifaceDef == null)
					return null;
				if (!visited.Add(ifaceDef))
					continue;
				switch (InterfacesInclude(ifaceDef, targetName, visited)) {
				case true: return true;
				case null: return null;
				}
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
