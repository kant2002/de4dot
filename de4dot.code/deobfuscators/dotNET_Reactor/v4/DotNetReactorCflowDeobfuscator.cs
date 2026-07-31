using System;
using System.Collections.Generic;
using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4 {
	/// <summary>
	///     Folds the call-based opaque predicates Reactor puts in front of its switch dispatchers:
	///     a block ending <c>call bool P(); brtrue/brfalse</c> whose fall-through starts with a
	///     <c>pop</c>, where <c>P</c> can only ever return one value.
	///
	///     "Can only ever" is the whole pass. The predicate is replaced by a constant and one of the
	///     two successors becomes unreachable, so a wrong answer deletes live code -- a failure that
	///     leaves the method verifiable and terminating and is therefore invisible to every gate we
	///     measure with. So the callee is folded only when its body is small enough to read in full
	///     and yields a constant on every path; anything with real computation in it, including the
	///     <c>return x > 0</c> that a shape heuristic mistakes for a predicate, is left alone.
	/// </summary>
	class DotNetReactorCflowDeobfuscator : IBlocksDeobfuscator {
		// Bisect lever, same purpose as the other DE4DOT_NO_* switches: attributing a change in the
		// output to this pass means turning it off and re-measuring, not arguing about it.
		static readonly bool FoldDisabled =
			Environment.GetEnvironmentVariable("DE4DOT_NO_PREDICATE_FOLD") == "1";

		bool isContainsSwitch;
		ModuleDef module;
		NeverWrittenStaticFields nullFields;

		public bool ExecuteIfNotModified { get; }

		/// <summary>
		///     Built on first use rather than in <see cref="DeobfuscateBegin" />: the scan walks
		///     every instruction in the module, and the site it serves occurs in a small minority of
		///     methods. Scoped to this instance so it cannot outlive the pipeline phase that built
		///     it -- a later phase may have removed the store that makes a field non-null.
		/// </summary>
		NeverWrittenStaticFields NullFields => nullFields ??= new NeverWrittenStaticFields(module);

		public void DeobfuscateBegin(Blocks blocks) {
			var contains = false;
			foreach (var instr in blocks.Method.Body.Instructions) {
				if (instr.OpCode == OpCodes.Switch) {
					contains = true;
					break;
				}
			}

			isContainsSwitch = contains;
			var currentModule = blocks.Method.Module;
			if (currentModule != module) {
				module = currentModule;
				nullFields = null;
			}
		}

		public bool Deobfuscate(List<Block> allBlocks) {
			if (!isContainsSwitch || FoldDisabled)
				return false;

			var modified = false;
			foreach (var block in allBlocks) {
				var instrs = block.Instructions;
				if (instrs.Count < 2)
					continue;
				var lastInstr = block.LastInstr;
				if (!lastInstr.IsBrtrue() && !lastInstr.IsBrfalse())
					continue;
				var callIndex = instrs.IndexOf(block.LastInstr) - 1;
				var call = instrs[callIndex];
				if (call.OpCode.Code != Code.Call)
					continue;
				if (block.FallThrough == null)
					continue;
				var pop = block.FallThrough.FirstInstr;
				if (pop.OpCode.Code != Code.Pop)
					continue;
				var method = call.Operand as MethodDef;
				if (method == null)
					continue;

				var branchValue = GetConstantBranchValue(method);
				if (branchValue == null) {
					Logger.v("Reactor cflow: predicate fold declined, {0} is not provably constant",
						Utils.RemoveNewlines(method));
					continue;
				}

				// Deleting the call deletes the field access that would have triggered the callee's
				// declaring type's .cctor. Reactor's own predicate types have none, and the shapes
				// accepted below can push nothing but a constant or a null field.
				block.Replace(callIndex, 1, branchValue.Value ? OpCodes.Ldc_I4_1.ToInstruction() : OpCodes.Ldc_I4_0.ToInstruction());
				Logger.v("Reactor cflow: folded predicate {0} to {1}",
					Utils.RemoveNewlines(method), branchValue.Value);

				modified = true;
			}

			return modified;
		}

		/// <summary>
		///     The value the method provably yields on every path when consumed by a brtrue/brfalse
		///     (true = non-zero/non-null, false = zero/null), or null when the method is anything
		///     other than one of the shapes below.
		///     <code>
		///       ldc.i4 X;  ret                      -> X != 0
		///       ldnull;    ret                      -> false            (returns null)
		///       ldsfld F;  ret                      -> false            (F is always null)
		///       A; B; ceq;    ret  (A, B both null) -> true             (null == null)
		///       A; B; cgt.un; ret  (A, B both null) -> false            (null != null)
		///     </code>
		///     where a provably-null operand is <c>ldnull</c> or an <c>ldsfld</c> of a field
		///     <see cref="NeverWrittenStaticFields" /> vouches for.
		/// </summary>
		bool? GetConstantBranchValue(MethodDef method) {
			// The call is replaced by a single push, so anything the call would have popped must be
			// nothing at all -- otherwise the arguments are stranded and the stack depth at the
			// branch, and at every successor merge, is wrong.
			if (!method.IsStatic || method.HasThis || method.MethodSig == null ||
				method.MethodSig.Params.Count != 0)
				return null;
			// A generic method's operand is a MethodSpec rather than a MethodDef, so this is only
			// belt and braces, but the body of an open generic proves nothing about any of its
			// instantiations.
			if (method.HasGenericParameters)
				return null;
			var body = method.Body;
			if (body == null || body.HasExceptionHandlers || body.HasVariables)
				return null;

			// Non-nop instruction stream. Stripping nops is safe here only because none of the
			// shapes below contains a branch, so no branch target can be dropped.
			var seq = new List<Instruction>();
			foreach (var instr in body.Instructions) {
				if (instr.OpCode.Code == Code.Nop)
					continue;
				seq.Add(instr);
			}
			if (seq.Count < 2 || seq[seq.Count - 1].OpCode.Code != Code.Ret)
				return null;

			// Single value + ret
			if (seq.Count == 2) {
				var v = seq[0];
				if (v.IsLdcI4())
					return v.GetLdcI4Value() != 0;
				if (IsProvablyNull(v))
					return false; // null -> brtrue not taken
				return null;
			}

			// Comparison of two provably-null operands + ret
			if (seq.Count == 4) {
				if (IsProvablyNull(seq[0]) && IsProvablyNull(seq[1])) {
					switch (seq[2].OpCode.Code) {
					case Code.Ceq:      // null == null -> 1 -> true
						return true;
					case Code.Cgt_Un:   // null != null -> 0 -> false
						return false;
					}
				}
			}

			return null;
		}

		bool IsProvablyNull(Instruction instr) {
			if (instr.OpCode.Code == Code.Ldnull)
				return true;
			if (instr.OpCode.Code != Code.Ldsfld)
				return false;
			return NullFields.IsProvablyNull(NeverWrittenStaticFields.GetField(instr));
		}
	}
}
