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
using System.Text;
using de4dot.blocks;
using dnlib.DotNet;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4.xorswitch;

/// <summary>
///     Provenance log for state seeds flowing through <see cref="EdgeResolver"/>.
///
///     <para>
///     Enabled by <c>DE4DOT_XORSWITCH_TRACE=&lt;method-name-substring&gt;</c>, so a single method can be
///     followed without drowning in a whole assembly's dispatches. Every seed this resolver acts on is
///     printed with **where it came from**, because the failure mode here is not a crash — it is a
///     plausible seed used in the wrong place, which yields an in-range case index and therefore
///     silently wrong control flow.
///     </para>
///
///     <para>
///     Blocks are identified by an ordinal assigned on first sight plus the first instruction's IL
///     offset. The ordinal is what makes the log readable: de4dot synthesises instructions with
///     <c>Offset == 0</c>, so several distinct blocks can all print as <c>IL_0000</c> and an
///     offset-keyed diagnostic then reads as one block.
///     </para>
/// </summary>
static class XorSwitchTrace {
	static readonly string Filter = Environment.GetEnvironmentVariable("DE4DOT_XORSWITCH_TRACE");

	public static bool Enabled => Filter is { Length: > 0 };

	static readonly Dictionary<Block, int> Ordinals = new();
	static string currentMethod = "";

	public static bool Wants(MethodDef method) =>
		Enabled && method is not null && method.FullName.IndexOf(Filter!, StringComparison.Ordinal) >= 0;

	public static void BeginMethod(MethodDef method) {
		currentMethod = method.DeclaringType is null
			? method.Name
			: method.DeclaringType.Name + "::" + method.Name;
		Ordinals.Clear();
	}

	public static string Id(Block block) {
		if (block is null)
			return "<null>";
		if (!Ordinals.TryGetValue(block, out int ordinal)) {
			ordinal = Ordinals.Count;
			Ordinals[block] = ordinal;
		}
		var offset = block.Instructions.Count > 0 ? block.Instructions[0].Instruction.Offset : 0;
		return $"b{ordinal}@IL_{offset:X4}";
	}

	/// <summary>First few opcodes of a block, to line it up against a dump of the original IL.</summary>
	public static string Sketch(Block block) {
		if (block is null)
			return "<null>";
		var sb = new StringBuilder();
		var instrs = block.Instructions;
		for (int i = 0; i < instrs.Count && i < 6; i++) {
			if (i > 0)
				sb.Append("; ");
			sb.Append(instrs[i].OpCode.Name);
			if (instrs[i].IsLdcI4())
				sb.Append(' ').Append(instrs[i].GetLdcI4Value());
		}
		if (instrs.Count > 6)
			sb.Append("; ...");
		return sb.ToString();
	}

	public static void Log(string message) => Logger.v("  [xsw:{0}] {1}", currentMethod, message);
}
