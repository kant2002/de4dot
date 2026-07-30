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
using System.IO;
using System.Reflection;

namespace de4dot.constdata {
	/// <summary>
	///     One-shot worker that extracts .NET Reactor's constant/string data array.
	///
	///     Why this is a separate process at all
	///     -------------------------------------
	///     Reactor builds the array in a static constructor, so obtaining it means loading the obfuscated
	///     assembly and running attacker-controlled code. de4dot used to do that in its own process,
	///     which coupled the whole tool to a runtime version: .NET 10's loader validates nested-type
	///     metadata more strictly and rejects Reactor output with
	///     <c>BadImageFormatException: Enclosing type(s) not found</c>, silently disabling all constant
	///     and string decryption. Pinning this worker to net8.0 lets the host move on independently.
	///
	///     The isolation is real but limited, and the limit matters: a child process gives deterministic
	///     runtime selection, containment of CLR crashes and <c>Environment.Exit</c>, an enforceable
	///     timeout, and no contamination of the host's loader state. It is NOT a security boundary — the
	///     target's static constructor still runs with this process's filesystem, network, environment
	///     and process-creation rights. Sandboxing that properly needs OS-level confinement around this
	///     executable (namespaces/seccomp, or AppContainer plus a Job Object) and is deliberately out of
	///     scope here.
	///
	///     Exits 0 on a well-formed response of either status, and non-zero only when it could not speak
	///     the protocol at all.
	/// </summary>
	static class Program {
		static int Main() {
			Stream stdout;
			try {
				stdout = Console.OpenStandardOutput();
			}
			catch (Exception) {
				return 2;
			}

			try {
				var stdin = Console.OpenStandardInput();
				var request = ReadRequest(stdin);
				byte[] data = Extract(request.path, request.fieldToken, request.maxResult);
				WriteOk(stdout, data);
				return 0;
			}
			catch (ProtocolException ex) {
				// Could not even parse the request, so the parent gets a diagnostic rather than silence.
				TryWriteError(stdout, "protocol: " + ex.Message);
				return 3;
			}
			catch (Exception ex) {
				// Extraction failure is an expected outcome, not a crash: report it and exit cleanly so
				// the parent can distinguish "no data" from "worker died".
				TryWriteError(stdout, ex.GetType().Name + ": " + ex.Message);
				return 0;
			}
		}

		sealed class ProtocolException : Exception {
			public ProtocolException(string message) : base(message) { }
		}

		static (string path, int fieldToken, int maxResult) ReadRequest(Stream stdin) {
			if (ReadUInt32(stdin) != ConstDataProtocol.Magic)
				throw new ProtocolException("bad request magic");

			int pathLen = ReadInt32(stdin);
			if (pathLen <= 0 || pathLen > ConstDataProtocol.MaxPathBytes)
				throw new ProtocolException($"path length {pathLen} out of range");
			string path = System.Text.Encoding.UTF8.GetString(ReadExactly(stdin, pathLen));

			int fieldToken = ReadInt32(stdin);
			int maxResult = ReadInt32(stdin);
			if (maxResult <= 0 || maxResult > ConstDataProtocol.MaxResultSizeLimit)
				throw new ProtocolException($"max result {maxResult} out of range");

			return (path, fieldToken, maxResult);
		}

		/// <summary>
		///     Loads the target and reads the data field. The field is normally on &lt;Module&gt;, which
		///     <c>Module.ResolveType</c> refuses, so the token is resolved directly as a field.
		/// </summary>
		static byte[] Extract(string path, int fieldToken, int maxResult) {
			var bytes = File.ReadAllBytes(path);
			var asm = Assembly.Load(bytes);            // runs the target's static constructors
			var mod = asm.GetModules()[0];

			byte[] Check(byte[] value) {
				if (value.Length > maxResult)
					throw new InvalidOperationException(
						$"data array is {value.Length} bytes, over the {maxResult} byte cap");
				return value;
			}

			try {
				var field = mod.ResolveField(fieldToken);
				if (field?.GetValue(null) is byte[] { Length: > 0 } direct)
					return Check(direct);
			}
			catch (Exception) {
				// fall through to the scan; the token may not survive de4dot's view of the module
			}

			foreach (var field in mod.GetFields(BindingFlags.Static | BindingFlags.NonPublic |
											   BindingFlags.Public)) {
				if (field.FieldType != typeof(byte[]))
					continue;
				try {
					if (field.GetValue(null) is byte[] { Length: > 0 } found)
						return Check(found);
				}
				catch (Exception) {
					// a field whose initialiser threw is not the one we want
				}
			}

			throw new InvalidOperationException("no non-empty static byte[] field found");
		}

		// ---- framing ------------------------------------------------------------------------------

		static void WriteOk(Stream stdout, byte[] data) {
			WriteUInt32(stdout, ConstDataProtocol.Magic);
			stdout.WriteByte(ConstDataProtocol.StatusOk);
			WriteInt32(stdout, data.Length);
			stdout.Write(data, 0, data.Length);
			stdout.Flush();
		}

		static void TryWriteError(Stream stdout, string message) {
			try {
				var payload = System.Text.Encoding.UTF8.GetBytes(message);
				if (payload.Length > 8192)
					Array.Resize(ref payload, 8192);
				WriteUInt32(stdout, ConstDataProtocol.Magic);
				stdout.WriteByte(ConstDataProtocol.StatusError);
				WriteInt32(stdout, payload.Length);
				stdout.Write(payload, 0, payload.Length);
				stdout.Flush();
			}
			catch (Exception) {
				// nothing useful left to do; the parent's timeout covers this
			}
		}

		static byte[] ReadExactly(Stream s, int count) {
			var buf = new byte[count];
			int read = 0;
			while (read < count) {
				int n = s.Read(buf, read, count - read);
				if (n <= 0)
					throw new ProtocolException($"stdin closed after {read} of {count} bytes");
				read += n;
			}
			return buf;
		}

		static uint ReadUInt32(Stream s) => BitConverter.ToUInt32(ReadExactly(s, 4), 0);
		static int ReadInt32(Stream s) => BitConverter.ToInt32(ReadExactly(s, 4), 0);
		static void WriteUInt32(Stream s, uint v) => s.Write(BitConverter.GetBytes(v), 0, 4);
		static void WriteInt32(Stream s, int v) => s.Write(BitConverter.GetBytes(v), 0, 4);
	}
}
