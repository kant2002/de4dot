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

namespace de4dot.blocks.cflow {
	/// <summary>
	///     A blocks deobfuscator that redirects switch dispatch to concrete targets, and can be asked
	///     not to.
	///
	///     Resolving a dispatch is the one rewrite here that can turn a correct method into a wrong one
	///     while every structural check still passes: the result is type-safe, stack-balanced, not
	///     empty, and still spins forever with its <c>ret</c> reachable in the graph but never
	///     dispatched to. Only tracing the finished method reveals it. So the pipeline builds the method
	///     both ways and keeps the resolved form only when its trace terminates, which needs a way to
	///     re-run the identical passes with just this one rewrite switched off.
	///
	///     Implementations must suppress the redirect itself, not their whole pass: constant folding and
	///     other preprocessing a resolver does should keep happening, so the two candidates differ by
	///     exactly the decision under test.
	/// </summary>
	public interface ISwitchDispatchResolver {
		bool SuppressDispatchResolution { get; set; }
	}
}
