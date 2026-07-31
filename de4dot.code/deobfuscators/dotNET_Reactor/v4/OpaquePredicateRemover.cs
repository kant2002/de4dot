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
using de4dot.blocks;
using de4dot.code;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4 {
	/// <summary>
	///     Removes the dead opaque-predicate pairs Reactor injects into a type: a static field whose
	///     type is the declaring type itself and which nothing ever assigns, plus a parameterless
	///     static bool whose whole body is <c>return thatField == null;</c>.
	///
	///     Because nothing assigns the field, the predicate is a constant <c>true</c>. Both members
	///     are scaffolding: they carry no program meaning, they survive every other pass because
	///     each is individually well-formed, and they inflate the member count of almost every type
	///     in the output.
	///
	///     Removal is deliberately narrow. A pair is only dropped when the field is read exactly
	///     once in the whole module -- by its own predicate -- is never written and never has its
	///     address taken, and the predicate itself is referenced by nothing at all. Anything that
	///     could observe either member, including a single unexplained reference, disqualifies the
	///     pair and leaves it in place. That matters because the shape is cheap to imitate: a real
	///     lazily-initialised singleton looks almost identical and differs only in having a store.
	/// </summary>
	class OpaquePredicateRemover {
		// Bisect lever, same purpose as the other DE4DOT_NO_* switches: attributing a change in the
		// output to this pass means turning it off and re-measuring, not arguing about it.
		static readonly bool Disabled =
			System.Environment.GetEnvironmentVariable("DE4DOT_NO_OPAQUE_PREDICATES") == "1";

		readonly ModuleDefMD module;
		readonly HashSet<MethodDef> doomed;
		readonly List<FieldDef> deadFields = new List<FieldDef>();
		readonly List<MethodDef> deadPredicates = new List<MethodDef>();

		public IEnumerable<FieldDef> Fields => deadFields;
		public IEnumerable<MethodDef> Predicates => deadPredicates;

		public int Count => deadFields.Count;

		/// <param name="alreadyDoomed">
		///     Methods earlier passes have queued for removal. They are still in the module -- queuing
		///     only deletes at the end of <c>DeobfuscateEnd</c> -- but a read from one of them is not
		///     a reason to keep a field, because the reader is going away too. Without this the pass
		///     finds nothing at all: Reactor's other injected methods read these same fields.
		/// </param>
		public OpaquePredicateRemover(ModuleDefMD module, IEnumerable<MethodDef> alreadyDoomed) {
			this.module = module;
			doomed = new HashSet<MethodDef>(alreadyDoomed);
		}

		public void Find() {
			if (Disabled) {
				Logger.n("Opaque predicates: DE4DOT_NO_OPAQUE_PREDICATES=1, leaving them in place");
				return;
			}

			var candidates = new List<(FieldDef Field, MethodDef Predicate)>();
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					var field = GetTestedField(method);
					if (field != null && field.DeclaringType == type && IsSelfTypedStatic(field, type))
						candidates.Add((field, method));
				}
			}
			if (candidates.Count == 0)
				return;

			// A field can back more than one predicate: at this point in the pipeline Reactor's other
			// injected methods are still present, and several of them read the same field.
			var byField = new Dictionary<FieldDef, List<MethodDef>>();
			foreach (var candidate in candidates) {
				if (!byField.TryGetValue(candidate.Field, out var list))
					byField[candidate.Field] = list = new List<MethodDef>();
				list.Add(candidate.Predicate);
			}

			var allPredicates = new List<MethodDef>();
			foreach (var entry in byField)
				allPredicates.AddRange(entry.Value);
			CountReferences(byField.Keys, allPredicates, out var readers, out var written, out var callers);

			int observed = 0, assigned = 0, called = 0;
			foreach (var entry in byField) {
				var field = entry.Key;
				var predicates = entry.Value;
				// A write anywhere means the field can be non-null, so the test is real code.
				if (written.Contains(field)) {
					assigned++;
					continue;
				}
				// Every reader must be going away too -- either a predicate removed with this field,
				// or a method some earlier pass already queued. A reader we keep would be left
				// referring to a field that no longer exists.
				var surviving = new HashSet<MethodDef>(readers[field]);
				surviving.ExceptWith(predicates);
				surviving.ExceptWith(doomed);
				if (surviving.Count != 0) {
					observed++;
					continue;
				}
				if (predicates.Exists(p => SurvivingCaller(callers[p], predicates))) {
					called++;
					continue;
				}
				deadFields.Add(field);
				deadPredicates.AddRange(predicates);
			}
			// The dead count is the interesting number, but the three rejection reasons are what
			// tell you whether a drop in it means the obfuscator changed or this pass got stricter.
			Logger.v("Opaque predicates: {0} field(s) and {1} method(s) dead of {2} candidate(s); "
				+ "kept {3} read elsewhere, {4} assigned, {5} called",
				deadFields.Count, deadPredicates.Count, candidates.Count, observed, assigned, called);
		}

		/// <summary>True when any caller will still be there after this round of removals.</summary>
		bool SurvivingCaller(HashSet<MethodDef> methodCallers, List<MethodDef> goingAway) {
			foreach (var caller in methodCallers) {
				if (!doomed.Contains(caller) && !goingAway.Contains(caller))
					return true;
			}
			return false;
		}

		static bool IsSelfTypedStatic(FieldDef field, TypeDef type) =>
			field.IsStatic && field.FieldType.TryGetTypeDef() == type;

		/// <summary>
		///     The field a method tests, when the method is exactly <c>return field == null;</c> and
		///     nothing else. Returns null for any other shape.
		/// </summary>
		static FieldDef GetTestedField(MethodDef method) {
			if (!method.IsStatic || method.Body == null || method.HasGenericParameters)
				return null;
			if (method.IsVirtual || method.HasOverrides || method.IsRuntimeSpecialName)
				return null;
			if (!DotNetUtils.IsMethod(method, "System.Boolean", "()"))
				return null;
			if (method.Body.HasExceptionHandlers || method.Body.HasVariables)
				return null;

			var instrs = method.Body.Instructions;
			int i = 0;
			if (instrs.Count != 4)
				return null;
			if (instrs[i].OpCode.Code != Code.Ldsfld)
				return null;
			var field = instrs[i++].Operand as FieldDef;
			if (field == null)
				return null;
			if (instrs[i++].OpCode.Code != Code.Ldnull)
				return null;
			if (instrs[i++].OpCode.Code != Code.Ceq)
				return null;
			if (instrs[i].OpCode.Code != Code.Ret)
				return null;
			return field;
		}

		/// <summary>
		///     One pass over every instruction in the module, counting what touches the candidates.
		///     Counting is module-wide on purpose: a reference from anywhere -- including another
		///     candidate's body -- is a reason to keep the pair.
		/// </summary>
		void CountReferences(IEnumerable<FieldDef> watchedFieldsSource,
				IEnumerable<MethodDef> watchedMethodsSource,
				out Dictionary<FieldDef, HashSet<MethodDef>> readers,
				out HashSet<FieldDef> written,
				out Dictionary<MethodDef, HashSet<MethodDef>> callers) {
			var watchedFields = new HashSet<FieldDef>(watchedFieldsSource);
			var watchedMethods = new HashSet<MethodDef>(watchedMethodsSource);
			readers = new Dictionary<FieldDef, HashSet<MethodDef>>();
			foreach (var field in watchedFields)
				readers[field] = new HashSet<MethodDef>();
			written = new HashSet<FieldDef>();
			callers = new Dictionary<MethodDef, HashSet<MethodDef>>();
			foreach (var m in watchedMethods)
				callers[m] = new HashSet<MethodDef>();

			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (method.Body == null)
						continue;
					foreach (var instr in method.Body.Instructions) {
						// The load-only rule and the MemberRef resolution both live in
						// NeverWrittenStaticFields, which the cflow predicate fold reads too: an
						// address, a store, or anything else naming the field -- including an
						// ldtoken handing it to reflection -- is a possible write.
						if (NeverWrittenStaticFields.GetField(instr) is { } field && watchedFields.Contains(field)) {
							if (NeverWrittenStaticFields.IsLoad(instr.OpCode.Code))
								readers[field].Add(method);
							else
								written.Add(field);
						}
						else if (instr.Operand is MethodDef target && watchedMethods.Contains(target))
							callers[target].Add(method);
					}
				}
			}
		}
	}
}
