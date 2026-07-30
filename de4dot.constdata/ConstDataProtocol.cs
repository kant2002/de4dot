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

namespace de4dot.constdata {
	/// <summary>
	///     Wire format for the constant-data extraction worker.
	///
	///     Deliberately fixed-shape and binary, with no object graph anywhere: the worker exists because
	///     extraction has to load and execute a hostile assembly, and a general-purpose serializer on
	///     that boundary would hand the hostile side an attack surface bigger than the problem being
	///     solved. Everything is length-prefixed little-endian, and the parent validates every length
	///     against a cap it chose before reading a single payload byte.
	///
	///     Request  (parent -> child stdin):  MAGIC, i32 pathLen, UTF8 path, i32 fieldToken, i32 maxResult
	///     Response (child -> parent stdout): MAGIC, u8 status, then
	///                                          status 0: i32 len, len bytes
	///                                          status 1: i32 len, UTF8 message
	///
	///     One assembly per process, one request, one response, exit. There is no session and no second
	///     message, so a worker cannot be reused after it has executed target code.
	/// </summary>
	public static class ConstDataProtocol {
		/// <summary>Guards against a worker/host version mismatch and against stray stdout noise.</summary>
		public const uint Magic = 0x31525844; // "DXR1"

		public const byte StatusOk = 0;
		public const byte StatusError = 1;

		/// <summary>
		///     Hard ceiling the worker refuses to exceed regardless of what the parent asks for, so a
		///     corrupted or hostile length cannot make either side allocate without bound. Reactor's
		///     arrays measure in tens of kilobytes; 64 MiB is far above anything legitimate.
		/// </summary>
		public const int MaxResultSizeLimit = 64 * 1024 * 1024;

		/// <summary>Longest path the worker will accept, to bound the first allocation it makes.</summary>
		public const int MaxPathBytes = 32 * 1024;

		/// <summary>
		///     Metadata table index of a FieldDef. A field token is <c>(0x04 &lt;&lt; 24) | rid</c>,
		///     so the high byte identifies the table and the low 24 bits the row.
		/// </summary>
		public const int FieldTableIndex = 0x04;
	}
}
