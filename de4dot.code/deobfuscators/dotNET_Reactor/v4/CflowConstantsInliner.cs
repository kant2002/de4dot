using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4 {
	// Reactor can move integer constants out of the code and into static fields of one big sealed
	// type, initialised by a single straight-line method. This pass recovers them: constant-fold that
	// method, harvest every `ldc.i4; stsfld` pair, then replace every `ldsfld` of a harvested field
	// with the literal, module-wide.
	//
	// The initialiser is selected purely by SHAPE (a static, assembly-visible method on a sealed type
	// with >=100 fields, yielding >=100 constant stores), which says nothing about whether it runs.
	// That matters: if it does not, every field keeps its default 0 at runtime, and inlining the
	// harvested values is a semantic change rather than a recovery. Because these constants feed
	// opaque predicates, the damage would be a silently different branch, not invalid IL -- the
	// hardest kind of defect to notice and the easiest to propagate.
	//
	// So shape only proposes; IsInitializerExecuted decides, during selection rather than after it.
	// A candidate is taken only when its declaring type's .cctor calls it, which is what actually
	// makes the stores precede every read being rewritten; a candidate that fails simply loses its
	// turn, and the search goes on. If none qualifies, the constants are left alone -- encrypted
	// constants are recoverable, and a wrong branch is not.
	class CflowConstantsInliner {
		public TypeDef Type;

		ModuleDefMD module;
		ISimpleDeobfuscator simpleDeobfuscator;
		MethodDef initializer;
		// Shape-matching candidates that could not be shown to run. Kept only to explain a refusal.
		List<MethodDef> rejected = new List<MethodDef>();
		Dictionary<FieldDef, int> dictionary = new Dictionary<FieldDef, int>();

		public CflowConstantsInliner(ModuleDefMD module, ISimpleDeobfuscator simpleDeobfuscator) {
			this.module = module;
			this.simpleDeobfuscator = simpleDeobfuscator;
			Find();
		}

		void Find() {
			foreach (var type in module.GetTypes()) {
				if (type.IsSealed && type.HasFields) {
					if (type.Fields.Count < 100)
						continue;
					foreach (var method in type.Methods) {
						if (!method.IsStatic)
							continue;
						if (!method.IsAssembly)
							continue;
						if (!method.HasBody)
							continue;

						simpleDeobfuscator.Deobfuscate(method);

						var instrs = method.Body.Instructions;
						for (var i = 0; i < instrs.Count; i++) {
							var ldcI4 = instrs[i];
							if (!ldcI4.IsLdcI4())
								continue;
							if (i + 1 >= instrs.Count)
								continue;
							var stsfld = instrs[i + 1];
							if (stsfld.OpCode.Code != Code.Stsfld)
								continue;
							var key = stsfld.Operand as FieldDef;
							if (key == null)
								continue;

							var value = ldcI4.GetLdcI4Value();
							if (!dictionary.ContainsKey(key))
								dictionary.Add(key, value);
							else
								dictionary[key] = value;
						}

						if (dictionary.Count < 100) {
							dictionary.Clear();
							continue;
						}

						// Shape proposes, the premise decides -- and it decides HERE rather than
						// after the fact, so a candidate that cannot be shown to run just loses its
						// turn instead of vetoing the whole pass. An assembly whose second candidate
						// is the real initialiser folds correctly; treating the first match as final
						// would silently forfeit it.
						if (!IsInitializerExecuted(method)) {
							rejected.Add(method);
							dictionary.Clear();
							continue;
						}

						Type = type;
						initializer = method;
						ReportRefused(selected: true);
						return;
					}
				}
			}

			ReportRefused(selected: false);
		}

		/// <summary>
		/// Account for every shape match that failed the premise, whether or not a later one passed.
		/// </summary>
		/// <remarks>
		/// The severity is the signal, not the wording. Skipping a candidate while another qualified
		/// leaves nothing wrong, so it is verbose detail; finishing selection having folded nothing
		/// means the pass silently did not run, which is worth a warning.
		///
		/// Either way this is the failure path, and only the failure path, which is why it can afford
		/// the module-wide referrer scan that <see cref="IsInitializerExecuted"/> deliberately avoids.
		/// Naming the referrers is the useful part: "called from an ordinary method" and "called from
		/// nowhere at all" are different problems, and the message should not make the reader guess.
		/// </remarks>
		void ReportRefused(bool selected) {
			foreach (var candidate in rejected) {
				var message = "Cflow constants: not folding constants from {0} -- its declaring type's .cctor does not call it, so nothing establishes the fields are written before they are read ({1})";
				var name = Utils.RemoveNewlines(candidate);
				var where = DescribeReferrers(FindReferrers(candidate));
				if (selected)
					Logger.v(message, name, where);
				else
					Logger.w(message, name, where);
			}
		}

		// Bisect lever. These constants feed opaque predicates, so folding them decides branches --
		// which makes "was it this pass?" a question worth being able to answer in one run rather than
		// by rebuilding with an edit. Set to 1 to leave the fields alone.
		static readonly bool Disabled = Environment.GetEnvironmentVariable("DE4DOT_NO_CFLOW_CONSTANTS") == "1";

		public void InlineAllConstants() {
			if (dictionary.Count == 0)
				return;

			if (Disabled) {
				Logger.n("Cflow constants: DE4DOT_NO_CFLOW_CONSTANTS=1, leaving {0} constant(s) from {1} unfolded",
					dictionary.Count, Utils.RemoveNewlines(initializer));
				// Same coupling as a refusal: the caller removes Type, and removing it while the
				// ldsfld sites still read those fields would be worse than not folding.
				Type = null;
				dictionary.Clear();
				return;
			}

			// No premise check here: Find() only selects a candidate whose .cctor calls it, so
			// reaching this point already means the stores precede every read being rewritten. A
			// second check would be a second copy of that rule, free to disagree with the first.
			int inlined = 0;
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (!method.HasBody)
						continue;

					var instrs = method.Body.Instructions;

					for (var i = 0; i < instrs.Count; i++) {
						var ldsfld = instrs[i];
						if (ldsfld.OpCode.Code != Code.Ldsfld)
							continue;
						var ldsfldValue = ldsfld.Operand as FieldDef;
						if (ldsfldValue == null)
							continue;
						if (dictionary.TryGetValue(ldsfldValue, out var value)) {
							instrs[i] = Instruction.CreateLdcI4(value);
							inlined++;
						}
					}
				}
			}

			Logger.v("Cflow constants: {0} constant(s) from {1}, inlined at {2} site(s) (called by its declaring type's .cctor)",
				dictionary.Count, Utils.RemoveNewlines(initializer), inlined);
		}

		/// <summary>
		/// Every method that names <paramref name="target"/> in its body. Diagnostics only.
		/// </summary>
		/// <remarks>
		/// Any operand that names it counts, not just call: ldftn, newobj and ldtoken are all routes,
		/// and a delegate built from ldftn is invoked somewhere this scan cannot see. That keeps an
		/// empty result meaning "nothing in managed metadata mentions it" rather than "not called".
		///
		/// This deliberately does NOT feed the decision. Being referenced says nothing about ordering
		/// -- see <see cref="IsInitializerExecuted"/> -- so using it to decide would be a check that
		/// looks like it establishes the premise while establishing nothing.
		/// </remarks>
		List<MethodDef> FindReferrers(MethodDef target) {
			var referrers = new List<MethodDef>();
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (!method.HasBody || method == target || referrers.Contains(method))
						continue;
					foreach (var instr in method.Body.Instructions) {
						if (instr.Operand is IMethod called && AsMethodDef(called) == target) {
							referrers.Add(method);
							break;
						}
					}
				}
			}
			return referrers;
		}

		/// <summary>
		/// Whether <paramref name="candidate"/> is guaranteed to have run before any of the fields it
		/// stores to is read -- the premise the fold depends on.
		/// </summary>
		/// <remarks>
		/// The guarantee comes from type-initialiser semantics, not from counting callers. The fields
		/// and the initialiser live on the same type, and the CLR runs a type's .cctor before the
		/// first static-field access on that type. So if the .cctor calls the initialiser, every read
		/// this pass would rewrite is necessarily preceded by the store.
		///
		/// A call from anywhere else does not give that, however plausible it looks: an ordinary
		/// method may or may not run, and if it does not, the fields keep their default 0 while the
		/// fold substitutes some other value. Because these constants feed opaque predicates, that
		/// surfaces as a silently different branch rather than as invalid IL -- so this deliberately
		/// refuses on anything short of the .cctor, which `cflow_called_outside_cctor` pins down.
		///
		/// Cheap on purpose: one .cctor body, no module walk, so Find() can afford to ask it of every
		/// candidate rather than only of the first.
		///
		/// Two things it does not establish, both deliberate. It only looks at direct calls, so a
		/// .cctor that reaches the initialiser through a helper is refused; that is a false negative
		/// costing readability, and widening it would mean proving the intermediate call happens.
		/// And it does not show the .cctor's call or the stores are unconditional -- they are
		/// straight-line in every sample observed, but a conditional store is Find()'s problem to
		/// notice, not this method's.
		/// </remarks>
		static bool IsInitializerExecuted(MethodDef candidate) {
			var cctor = candidate.DeclaringType.FindStaticConstructor();
			if (cctor == null)
				return false;
			// Reactor puts the stores in a separate method that .cctor calls, but a producer that put
			// them directly in .cctor would be the same guarantee arrived at one step earlier.
			if (cctor == candidate)
				return true;
			if (!cctor.HasBody)
				return false;
			foreach (var instr in cctor.Body.Instructions) {
				if (instr.Operand is IMethod called && AsMethodDef(called) == candidate)
					return true;
			}
			return false;
		}

		static string DescribeReferrers(List<MethodDef> referrers) {
			if (referrers.Count == 0)
				return "referenced by nothing in this module";
			var names = new List<string>();
			foreach (var referrer in referrers)
				names.Add(Utils.RemoveNewlines(referrer));
			return "referenced by " + string.Join(", ", names);
		}

		static MethodDef AsMethodDef(IMethod method) {
			if (method is MethodDef def)
				return def;
			if (method is MethodSpec spec)
				return AsMethodDef(spec.Method);
			return (method as IMethodDefOrRef)?.ResolveMethodDef();
		}
	}
}
