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
using System.Diagnostics;
using System.IO;
using System.Text;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4 {
	/// <summary>
	///     Parent side of the one-shot constant-data extraction worker.
	///
	///     Extraction has to load and run the obfuscated assembly's static constructors. Doing that in
	///     de4dot's own process couples the entire tool to a runtime version: .NET 10's loader rejects
	///     Reactor metadata, which silently disables every constant and string. This runs it in a net8.0
	///     child instead: one assembly per process, one request, one response, exit.
	///
	///     The child is a compatibility and crash boundary, not a security one — the target's static
	///     constructor runs with this process's rights either way. Confining that is the operator's
	///     concern; run de4dot in a container or VM if the input is not trusted.
	///
	///     The response is still parsed defensively, because a target that runs arbitrary code can make
	///     the child say anything: lengths are validated against a cap before any payload is read, the
	///     magic must match, and a worker that overruns its timeout has its process tree killed.
	/// </summary>
	static class ConstantDataWorker {
		/// <summary>Set to a worker path to override discovery; useful when the layout is unusual.</summary>
		const string WorkerPathVar = "DE4DOT_CONSTDATA_WORKER";

		/// <summary>Subdirectory of the host that holds the self-contained worker.</summary>
		const string WorkerSubdir = "constdata";

		/// <summary>
		///     Generous enough for a real .cctor that decrypts tens of kilobytes, short enough that a
		///     target which hangs or waits on input does not stall the run.
		/// </summary>
		static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

		/// <summary>Result cap sent to the worker. Reactor's arrays are tens of KiB.</summary>
		const int MaxResultSize = 16 * 1024 * 1024;

		const uint Magic = 0x31525844; // "DXR1", must match ConstDataProtocol
		const byte StatusOk = 0;

		/// <summary>Error text is for a log line, not a payload; keep it tiny.</summary>
		const int MaxErrorSize = 4 * 1024;

		/// <summary>Cap on stderr buffered in the host: a diagnostic, not a transcript.</summary>
		const int MaxStderrChars = 8 * 1024;

		/// <summary>
		///     Runs the worker. Returns the extracted array, or null if it could not be produced for any
		///     reason, in which case the caller falls back to in-process extraction. Never throws.
		/// </summary>
		public static byte[] TryExtract(string assemblyPath, int fieldToken) {
			string worker = FindWorker();
			if (worker is null) {
				Logger.v("Constant-data worker not found");
				return null;
			}

			Process process = null;
			try {
				var psi = new ProcessStartInfo(worker) {
					RedirectStandardInput = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					// A clean working directory keeps a target that writes relative paths from
					// scribbling into the output tree.
					WorkingDirectory = Path.GetTempPath(),
				};

				process = Process.Start(psi);
				if (process is null)
					return null;

				// stderr is redirected, so it MUST be drained: an undrained pipe fills at a few tens
				// of KB and the worker then blocks forever on write. The timeout would cover that, but
				// as a timeout -- reporting a stall whose actual cause was a chatty target. Draining
				// asynchronously also turns what was discarded output into a diagnostic. Bounded,
				// because a target that floods stderr must not be buffered without limit in the host.
				var stderrTail = new StringBuilder();
				process.ErrorDataReceived += (_, e) => {
					if (e.Data is null)
						return;
					lock (stderrTail) {
						if (stderrTail.Length < MaxStderrChars)
							stderrTail.Append(e.Data).Append('\n');
					}
				};
				process.BeginErrorReadLine();

				WriteRequest(process.StandardInput.BaseStream, assemblyPath, fieldToken);

				// Read on this thread, but bound the whole exchange by the timeout below: the response is
				// tiny and arrives before exit, so a stalled worker shows up as a read that never
				// completes rather than as a wait that never returns.
				byte[] result = null;
				string error = null;
				var reader = new System.Threading.Thread(() => {
					try {
						result = ReadResponse(process.StandardOutput.BaseStream, out error);
					}
					catch (Exception ex) {
						error = ex.GetType().Name + ": " + ex.Message;
					}
				}) { IsBackground = true };
				reader.Start();

				if (!reader.Join(Timeout)) {
					Logger.w("Constant-data worker timed out after {0}s; killing it", Timeout.TotalSeconds);
					KillTree(process);
					LogStderr(stderrTail);
					return null;
				}

				if (!process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds))
					KillTree(process);

				if (result is null) {
					Logger.v("Constant-data worker reported: {0}", error ?? "no data");
					LogStderr(stderrTail);
					return null;
				}
				Logger.v("Constant-data worker extracted {0} bytes", result.Length);
				return result;
			}
			catch (Exception ex) {
				Logger.v("Constant-data worker failed: {0}: {1}", ex.GetType().Name, ex.Message);
				if (process is not null)
					KillTree(process);
				return null;
			}
			finally {
				process?.Dispose();
			}
		}

		/// <summary>Keeps only printable characters, so worker text cannot inject terminal escapes.</summary>
		static string Sanitize(string s) {
			var sb = new StringBuilder(s.Length);
			foreach (char c in s) {
				if (c == '\t' || (!char.IsControl(c) && c != '\u001b'))
					sb.Append(c);
				else
					sb.Append('.');
			}
			return sb.ToString();
		}

		static void KillTree(Process process) {
			try {
				if (!process.HasExited) {
#if NETFRAMEWORK
						// net48 has no process-tree kill. The worker spawns nothing itself, so killing it
						// directly is equivalent unless the TARGET spawned something -- containing that is one
						// more thing only the modern path can do.
						process.Kill();
#else
						process.Kill(entireProcessTree: true);
#endif
					}
			}
			catch (Exception) {
				// already gone, or not killable; the caller falls back regardless
			}
		}

		/// <summary>Report what the worker wrote to stderr, if anything, on a failure path.</summary>
		static void LogStderr(StringBuilder tail) {
			string text;
			lock (tail)
				text = tail.ToString();
			if (text.Length == 0)
				return;
			Logger.v("Constant-data worker stderr:");
			foreach (var line in text.Split('\n')) {
				if (line.Length > 0)
					Logger.v("  {0}", Utils.RemoveNewlines(line));
			}
		}

		static void WriteRequest(Stream stdin, string assemblyPath, int fieldToken) {
			var path = Encoding.UTF8.GetBytes(Path.GetFullPath(assemblyPath));
			stdin.Write(BitConverter.GetBytes(Magic), 0, 4);
			stdin.Write(BitConverter.GetBytes(path.Length), 0, 4);
			stdin.Write(path, 0, path.Length);
			stdin.Write(BitConverter.GetBytes(fieldToken), 0, 4);
			stdin.Write(BitConverter.GetBytes(MaxResultSize), 0, 4);
			stdin.Flush();
			stdin.Close();     // the worker reads exactly one request, so signal end of input
		}

		/// <summary>
		///     Reads one response. Returns the payload on success, or null with <paramref name="error"/>
		///     set. Validates the magic and every length before allocating.
		/// </summary>
		static byte[] ReadResponse(Stream stdout, out string error) {
			error = null;
			if (BitConverter.ToUInt32(ReadExactly(stdout, 4), 0) != Magic) {
				error = "bad response magic";
				return null;
			}

			int status = stdout.ReadByte();
			if (status < 0) {
				error = "stdout closed before status";
				return null;
			}

			int length = BitConverter.ToInt32(ReadExactly(stdout, 4), 0);
			if (length < 0 || length > MaxResultSize) {
				error = $"response length {length} out of range";
				return null;
			}

			// The worker ran the target's static constructors, so everything it says is
			// attacker-influenced. An error string in particular is text that ends up in a terminal:
			// cap it far below the data cap and strip anything that is not printable, so a hostile
			// payload cannot smuggle escape sequences or megabytes of noise into the log.
			if (status != StatusOk && length > MaxErrorSize) {
				error = $"error payload {length} bytes, over the {MaxErrorSize} byte cap";
				return null;
			}

			var payload = ReadExactly(stdout, length);

			// stdout carries the framed response and nothing else. Anything after it means the worker
			// wrote outside the protocol, so the response cannot be trusted as complete.
			if (stdout.ReadByte() >= 0) {
				error = "trailing bytes after response";
				return null;
			}

			if (status == StatusOk)
				return length > 0 ? payload : null;

			error = "worker: " + Sanitize(Encoding.UTF8.GetString(payload));
			return null;
		}

		static byte[] ReadExactly(Stream s, int count) {
			var buf = new byte[count];
			int read = 0;
			while (read < count) {
				int n = s.Read(buf, read, count - read);
				if (n <= 0)
					throw new EndOfStreamException($"stream closed after {read} of {count} bytes");
				read += n;
			}
			return buf;
		}

		/// <summary>
		///     Locates the worker: explicit override, then beside de4dot, then a sibling framework
		///     directory (the dev layout puts the net8.0 worker next to a net10.0 host).
		/// </summary>
		static string FindWorker() {
			string exe = "de4dot.constdata" + (Path.DirectorySeparatorChar == '\\' ? ".exe" : "");

			var overridePath = Environment.GetEnvironmentVariable(WorkerPathVar);
			if (!string.IsNullOrEmpty(overridePath))
				return File.Exists(overridePath) ? overridePath : null;

			var baseDir = AppContext.BaseDirectory;
			var beside = Path.Combine(baseDir, exe);
			if (File.Exists(beside))
				return beside;

			// Published layout: the worker is self-contained and carries its own .NET 8 runtime, so it
			// CANNOT sit flat beside a host built for a different framework -- both ship libcoreclr.so
			// and friends at different versions and would collide. It goes in a subdirectory instead.
			var nested = Path.Combine(baseDir, WorkerSubdir, exe);
			if (File.Exists(nested))
				return nested;

			// .../Release/<tfm>/<rid>/  ->  probe .../Release/*/<rid>/
			try {
				var rid = new DirectoryInfo(baseDir.TrimEnd(Path.DirectorySeparatorChar));
				var tfm = rid.Parent;
				var release = tfm?.Parent;
				if (release is not null) {
					foreach (var dir in release.GetDirectories()) {
						var candidate = Path.Combine(dir.FullName, rid.Name, exe);
						if (File.Exists(candidate))
							return candidate;
					}
				}
			}
			catch (Exception) {
				// probing is best-effort; absence just means fall back
			}
			return null;
		}
	}
}
