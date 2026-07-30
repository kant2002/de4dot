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
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4;

/// <summary>
///     Removes .NET Reactor-injected static fields and methods from compiler-generated
///     closure types (DisplayClass). The obfuscator injects a static self-reference field
///     and a null-check method into each DisplayClass, which prevents ILSpy from recognizing
///     the type as a simple closure and inlining it as a lambda expression.
/// </summary>
class DisplayClassCleaner {
	readonly ModuleDefMD module;
	readonly List<FieldDef> fieldsToRemove = new();
	readonly List<MethodDef> methodsToRemove = new();

	public IReadOnlyList<FieldDef> FieldsToRemove => fieldsToRemove;
	public IReadOnlyList<MethodDef> MethodsToRemove => methodsToRemove;

	public DisplayClassCleaner(ModuleDefMD module) => this.module = module;

	public void Find() {
		foreach (var type in module.GetTypes()) {
			if (!IsCompilerGeneratedClosureType(type))
				continue;
			CleanType(type);
		}
		PruneReferencedRemovals();
	}

	/// <summary>
	///     Safety net: never remove a field or method that is still referenced by code which will
	///     REMAIN in the module. Removal does not scrub callers, so removing a live member leaves a
	///     dangling MemberRef → invalid metadata (MissingField/MissingMethod). Iterated to a fixpoint
	///     because dropping a method from the removal set makes it "remaining", which can in turn
	///     re-justify keeping a field it references.
	/// </summary>
	void PruneReferencedRemovals() {
		bool changed = true;
		while (changed && (fieldsToRemove.Count > 0 || methodsToRemove.Count > 0)) {
			changed = false;
			var removedMethods = new HashSet<MethodDef>(methodsToRemove);
			var fieldSet = new HashSet<FieldDef>(fieldsToRemove);
			var stillReferencedFields = new HashSet<FieldDef>();
			var stillReferencedMethods = new HashSet<MethodDef>();

			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (removedMethods.Contains(method) || !method.HasBody)
						continue;
					foreach (var instr in method.Body.Instructions) {
						if (fieldSet.Count > 0 && instr.Operand is IField fref &&
							fref.ResolveFieldDef() is { } fd && fieldSet.Contains(fd))
							stillReferencedFields.Add(fd);
						if (removedMethods.Count > 0 && instr.Operand is IMethod mref &&
							mref.ResolveMethodDef() is { } md && removedMethods.Contains(md))
							stillReferencedMethods.Add(md);
					}
				}
			}

			if (fieldsToRemove.RemoveAll(stillReferencedFields.Contains) > 0)
				changed = true;
			if (methodsToRemove.RemoveAll(stillReferencedMethods.Contains) > 0)
				changed = true;
		}
	}

	static bool IsCompilerGeneratedClosureType(TypeDef type) {
		if (type.Name is null)
			return false;
		var name = type.Name.String;
		// C# compiler generates <>c__DisplayClass for closures and <>c for static delegate caches
		return name.Contains("<>c__DisplayClass") || name == "<>c";
	}

	void CleanType(TypeDef type) {
		// Find static fields that are self-references (same type as the declaring type).
		// The C# compiler never generates these for DisplayClass types.
		// For <>c types, the compiler generates ONE static readonly (initonly) field for the
		// singleton instance (<>9, which may be renamed by the obfuscator). We preserve that
		// and only remove non-readonly self-reference fields that are Reactor injections.
		var injectedFields = new List<FieldDef>();
		foreach (var field in type.Fields) {
			if (!field.IsStatic)
				continue;
			if (!IsSameType(field.FieldType, type))
				continue;
			// Preserve the legitimate singleton field: it's static readonly (InitOnly)
			if (field.IsInitOnly)
				continue;
			injectedFields.Add(field);
		}

		if (injectedFields.Count == 0)
			return;

		// Find the static helpers Reactor injects alongside those fields. It emits a *pair* per
		// closure type — a null-check guard and a plain getter — and both have to go, because
		// PruneReferencedRemovals keeps any field a surviving method still reads. Recognising only
		// the guard therefore preserves the field, and the field is what stops the decompiler
		// treating the type as a closure it can inline into a lambda.
		var injectedFieldSet = new HashSet<FieldDef>(injectedFields);
		foreach (var method in type.Methods) {
			if (!method.IsStatic || method.IsConstructor)
				continue;
			if (IsInjectedNullCheckMethod(method, injectedFieldSet) ||
				IsInjectedFieldGetter(method, type, injectedFieldSet))
				methodsToRemove.Add(method);
		}

		fieldsToRemove.AddRange(injectedFields);
	}

	static bool IsSameType(TypeSig sig, TypeDef type) {
		var td = sig?.TryGetTypeDef();
		if (td is not null)
			return td == type;
		// Handle generic instantiations — the field type might be a GenericInstSig
		// wrapping the same generic type definition
		if (sig is GenericInstSig gis)
			return gis.GenericType?.TypeDef == type;
		return false;
	}

	/// <summary>
	///     Checks if a method is a .NET Reactor-injected null-check guard.
	///     Pattern: ldsfld &lt;field&gt;; ldnull; ceq; ret
	///     or:      ldsfld &lt;field&gt;; brfalse/brtrue ...; ldc.i4; ret; ldc.i4; ret
	/// </summary>
	static bool IsInjectedNullCheckMethod(MethodDef method, HashSet<FieldDef> injectedFields) {
		if (!method.HasBody || method.Body.Instructions is null)
			return false;

		var instrs = method.Body.Instructions;
		if (instrs.Count < 3)
			return false;

		// Must have exactly one parameter (none besides 'this' for static) and return bool
		if (method.Parameters.Count != 0)
			return false;
		if (method.ReturnType is null || method.ReturnType.ElementType != ElementType.Boolean)
			return false;

		// Check first instruction loads an injected field
		if (instrs[0].OpCode.Code != Code.Ldsfld)
			return false;
		if (instrs[0].Operand is not FieldDef loadedField || !injectedFields.Contains(loadedField))
			return false;

		// Pattern 1: ldsfld; ldnull; ceq; ret (4 instructions)
		if (instrs.Count == 4 &&
			instrs[1].OpCode.Code == Code.Ldnull &&
			instrs[2].OpCode.Code == Code.Ceq &&
			instrs[3].OpCode.Code == Code.Ret)
			return true;

		// Pattern 2: ldsfld; brfalse/brtrue; then ONLY constant loads, branches and returns.
		// The tail must do no real work (no calls, stores, arithmetic) — otherwise this is not a
		// pure null-check guard and removing it would drop live logic.
		if (instrs.Count >= 4 &&
			(instrs[1].OpCode.Code == Code.Brfalse || instrs[1].OpCode.Code == Code.Brfalse_S ||
			 instrs[1].OpCode.Code == Code.Brtrue || instrs[1].OpCode.Code == Code.Brtrue_S)) {
			for (int i = 2; i < instrs.Count; i++) {
				if (!IsTrivialGuardInstr(instrs[i]))
					return false;
			}
			return true;
		}

		return false;
	}

	/// <summary>
	///     Checks if a method is the .NET Reactor-injected getter for one of the injected fields:
	///     a static, parameterless <c>ldsfld &lt;field&gt;; ret</c> returning the declaring type.
	///
	///     <para>
	///     Deliberately exact — two instructions, nothing else — because this is the shape whose
	///     absence from the removal set silently preserves the field. Anything that does more than
	///     hand the field back is not this helper and is left alone.
	///     </para>
	/// </summary>
	static bool IsInjectedFieldGetter(MethodDef method, TypeDef type, HashSet<FieldDef> injectedFields) {
		if (!method.HasBody || method.Body.Instructions is null)
			return false;
		if (method.Parameters.Count != 0)
			return false;
		if (!IsSameType(method.ReturnType, type))
			return false;

		var instrs = method.Body.Instructions;
		if (instrs.Count != 2)
			return false;
		if (instrs[0].OpCode.Code != Code.Ldsfld)
			return false;
		if (instrs[0].Operand is not FieldDef loadedField || !injectedFields.Contains(loadedField))
			return false;
		return instrs[1].OpCode.Code == Code.Ret;
	}

	/// <summary>
	///     True for the only instructions a pure boolean null-check guard's tail may contain:
	///     integer constant loads, unconditional branches, nops, and returns.
	/// </summary>
	static bool IsTrivialGuardInstr(Instruction instr) {
		switch (instr.OpCode.Code) {
		case Code.Ldc_I4:
		case Code.Ldc_I4_S:
		case Code.Ldc_I4_0:
		case Code.Ldc_I4_1:
		case Code.Ldc_I4_2:
		case Code.Ldc_I4_3:
		case Code.Ldc_I4_4:
		case Code.Ldc_I4_5:
		case Code.Ldc_I4_6:
		case Code.Ldc_I4_7:
		case Code.Ldc_I4_8:
		case Code.Ldc_I4_M1:
		case Code.Br:
		case Code.Br_S:
		case Code.Nop:
		case Code.Ret:
			return true;
		default:
			return false;
		}
	}
}
