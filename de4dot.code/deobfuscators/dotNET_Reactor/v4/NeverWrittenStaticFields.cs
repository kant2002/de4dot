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

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4 {
	/// <summary>
	///     One home for the question "is this static field always null?", which two Reactor passes
	///     need and which is easy to answer wrongly in the dangerous direction.
	///
	///     The premise is that a static reference-type field nothing ever assigns still holds its
	///     default value, null. Reactor's opaque predicates are built on exactly that. Every user of
	///     this class deletes code on the strength of the answer, so the class is written to fail
	///     closed: it says "provably null" only when the module gives it no way to be wrong, and any
	///     gap in the evidence -- an operand it cannot resolve, a field something outside the module
	///     could reach -- is an answer of false, never a shrug.
	///
	///     The rule for what counts as an assignment is the load-only rule, not the stsfld rule:
	///     anything naming the field that is not a plain <c>ldsfld</c>/<c>ldfld</c> is treated as a
	///     write. That covers <c>stsfld</c>, but also <c>ldsflda</c> followed by a store through the
	///     address and <c>ldtoken</c> handing the field to reflection, neither of which emits a
	///     store at all. Widening the shapes this class recognises is safe; widening what counts as
	///     "not written" is not.
	/// </summary>
	class NeverWrittenStaticFields {
		readonly ModuleDef module;
		HashSet<FieldDef> written;
		// Set when a field-naming instruction could not be resolved to a FieldDef. The write may
		// have been to a field someone later asks about, so no answer can be trusted afterwards.
		bool blind;

		public NeverWrittenStaticFields(ModuleDef module) => this.module = module;

		/// <summary>
		///     True when <paramref name="field" /> is a static reference-type field that nothing in
		///     the module assigns and nothing outside the module can name. Scanning is deferred to
		///     the first call, because the callers only reach a candidate site on a small minority
		///     of methods and the scan walks every instruction in the module.
		/// </summary>
		public bool IsProvablyNull(FieldDef field) {
			if (field == null || !field.IsStatic)
				return false;
			// An unassigned value type is its zero value, not null, and a generic parameter type
			// could be instantiated as either.
			var fieldType = field.FieldType;
			if (fieldType == null || fieldType.IsValueType || fieldType.IsGenericParameter)
				return false;
			if (CanBeAssignedOutsideModule(field))
				return false;
			EnsureScanned();
			return !blind && !written.Contains(field);
		}

		/// <summary>
		///     The only two field accesses that leave the field's value alone. Everything else --
		///     a store, an address, a token -- is a possible assignment.
		/// </summary>
		public static bool IsLoad(Code code) => code == Code.Ldsfld || code == Code.Ldfld;

		/// <summary>
		///     The field an instruction names, resolving through the MemberRef encoding that a
		///     field on a generic type always uses, or null when the instruction names no field or
		///     names one this module cannot resolve.
		/// </summary>
		public static FieldDef GetField(Instruction instr) =>
			instr.Operand is IField fref ? fref.ResolveFieldDef() : null;

		void EnsureScanned() {
			if (written != null)
				return;
			written = new HashSet<FieldDef>();
			if (module == null) {
				// No module means no evidence, and no evidence must not read as "nothing is written".
				blind = true;
				return;
			}

			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (!method.HasBody)
						continue;
					foreach (var instr in method.Body.Instructions) {
						if (IsLoad(instr.OpCode.Code) || instr.Operand is not IField fref)
							continue;
						var field = fref.ResolveFieldDef();
						if (field != null) {
							written.Add(field);
							continue;
						}
						// A reference into another assembly cannot be to a field of this module, so
						// failing to resolve it costs nothing. One that claims to be ours and still
						// will not resolve -- Reactor does mangle metadata -- could be a write to
						// any field here, and there is then nothing left to prove.
						if (MayNameFieldInThisAssembly(fref)) {
							blind = true;
							return;
						}
					}
				}
			}
		}

		bool MayNameFieldInThisAssembly(IField fref) {
			var refAsm = fref.DeclaringType?.DefinitionAssembly;
			var thisAsm = module.Assembly;
			if (refAsm == null || thisAsm == null)
				return true;
			return UTF8String.CaseInsensitiveEquals(refAsm.Name, thisAsm.Name);
		}

		/// <summary>
		///     True when something outside this module could assign the field, which puts it beyond
		///     what a module-wide scan can prove. Reflection by name from another assembly defeats
		///     even this, and nothing can rule that out; the in-module reflection case is covered by
		///     the ldtoken half of the load-only rule.
		/// </summary>
		static bool CanBeAssignedOutsideModule(FieldDef field) {
			// Only the declaring type can name a private field, and it lives in this module.
			if (field.IsPrivate || field.IsFamilyAndAssembly)
				return false;
			if (!field.IsAssembly && IsReachableOutsideAssembly(field.DeclaringType))
				return true;
			// Assembly-scoped, either declared so or made so by an unexported declaring type. That
			// still reaches past this module if the assembly has other modules or names friends.
			var asm = field.Module?.Assembly;
			if (asm == null)
				return false;
			if (asm.Modules.Count > 1)
				return true;
			foreach (var ca in asm.CustomAttributes) {
				if (ca.TypeFullName == "System.Runtime.CompilerServices.InternalsVisibleToAttribute")
					return true;
			}
			return false;
		}

		static bool IsReachableOutsideAssembly(TypeDef type) {
			while (type != null) {
				if (!type.IsNested)
					return type.IsPublic;
				if (!type.IsNestedPublic && !type.IsNestedFamily && !type.IsNestedFamilyOrAssembly)
					return false;
				type = type.DeclaringType;
			}
			return false;
		}
	}
}
