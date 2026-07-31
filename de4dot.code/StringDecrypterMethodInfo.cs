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

namespace de4dot.code {
	/// <summary>
	///     Identifies a string decrypter method a deobfuscator has found.
	///
	///     Deobfuscators used to report these as bare metadata tokens. The token is still the
	///     identity; the type exists so that one which needs to say more about a decrypter than
	///     where it lives has somewhere to say it, rather than a second collection keyed by token
	///     that every caller has to remember to keep in step.
	/// </summary>
	public sealed class StringDecrypterMethodInfo {
		public StringDecrypterMethodInfo(int methodToken) => MethodToken = methodToken;

		public int MethodToken { get; }
	}
}
