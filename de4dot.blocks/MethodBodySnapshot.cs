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

namespace de4dot.blocks {
	/// <summary>
	///     A detached copy of a method body that can be written back over the method more than once.
	///
	///     It exists so the same normalized input can be run through the body-local passes twice and
	///     the better result kept — see <c>ObfuscatedFile.SelectDispatchCandidate</c>.
	///
	///     Locals are part of the snapshot on purpose. <see cref="Blocks"/> holds a live reference to
	///     <c>Body.Variables</c> and <c>OptimizeLocals()</c> prunes and reorders it in place, so a
	///     restore that put back only instructions and handlers would leave the restored <c>ldloc.2</c>
	///     addressing a different local than it did when the snapshot was taken, or none at all.
	/// </summary>
	public sealed class MethodBodySnapshot {
		readonly IList<Instruction> instructions;
		readonly IList<ExceptionHandler> exceptionHandlers;
		readonly List<Local> locals;
		readonly bool initLocals;
		readonly ushort maxStack;
		readonly bool keepOldMaxStack;

		MethodBodySnapshot(IList<Instruction> instructions, IList<ExceptionHandler> exceptionHandlers,
				List<Local> locals, bool initLocals, ushort maxStack, bool keepOldMaxStack) {
			this.instructions = instructions;
			this.exceptionHandlers = exceptionHandlers;
			this.locals = locals;
			this.initLocals = initLocals;
			this.maxStack = maxStack;
			this.keepOldMaxStack = keepOldMaxStack;
		}

		public static MethodBodySnapshot Capture(MethodDef method) {
			var body = method.Body;
			DotNetUtils.CopyBody(body.Instructions, body.ExceptionHandlers, out var instrs, out var handlers);
			return new MethodBodySnapshot(instrs, handlers, new List<Local>(body.Variables),
				body.InitLocals, body.MaxStack, body.KeepOldMaxStack);
		}

		/// <summary>
		///     Overwrite <paramref name="method"/>'s body with the snapshot. Each restore hands over a
		///     fresh clone, so the snapshot survives whatever the restored body is then put through.
		/// </summary>
		public void Restore(MethodDef method) {
			var body = method.Body;
			// Locals before instructions: the restored code addresses them both by object (ldloc.s)
			// and by index (ldloc.0), so the list has to be back in its original order first.
			body.Variables.Clear();
			foreach (var local in locals)
				body.Variables.Add(local);

			DotNetUtils.CopyBody(instructions, exceptionHandlers, out var instrs, out var handlers);
			DotNetUtils.RestoreBody(method, instrs, handlers);
			body.InitLocals = initLocals;
			body.MaxStack = maxStack;
			body.KeepOldMaxStack = keepOldMaxStack;
		}
	}
}
