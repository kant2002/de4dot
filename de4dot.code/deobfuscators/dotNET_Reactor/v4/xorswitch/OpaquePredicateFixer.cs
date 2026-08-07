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

using de4dot.blocks;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4.xorswitch;

/// <summary>
///     Preprocessing: simplifies known junk patterns before abstract interpretation runs.
/// </summary>
static class OpaquePredicateFixer {
	/// <summary>
	///     Fold <c>ldc X; ldc X; pop</c> → <c>ldc X</c> (opaque predicate noise).
	/// </summary>
	public static bool Fold(Block block) {
		var instrs = block.Instructions;
		bool changed = false;

		for (int i = instrs.Count - 3; i >= 0; i--) {
			if (!instrs[i].IsLdcI4())
				continue;
			if (!instrs[i + 1].IsLdcI4())
				continue;
			if (!instrs[i + 2].IsPop())
				continue;
			if (instrs[i].GetLdcI4Value() != instrs[i + 1].GetLdcI4Value())
				continue;

			// Remove the duplicate ldc + pop, keep the first ldc
			instrs.RemoveAt(i + 2);
			instrs.RemoveAt(i + 1);
			changed = true;
		}

		return changed;
	}

}
