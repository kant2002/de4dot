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
using dnlib.DotNet;

namespace de4dot.code.deobfuscators {
	/// <summary>
	///     Extracts assemblies embedded by Costura.Fody.
	/// </summary>
	/// <remarks>
	///     Costura is a packer, not an obfuscator: it moves an application's dependencies into
	///     resources of the main assembly and adds an <c>AssemblyResolve</c> handler that loads them
	///     from there at runtime. It is deliberately not tied to any one obfuscator here, because it
	///     composes with all of them — an obfuscated assembly is frequently a Costura host, and the
	///     dependencies inside it are usually obfuscated too, so they are worth getting out where they
	///     can be deobfuscated in their own right.
	///
	///     The format is simple and stable: one resource per file, named
	///     <c>costura.&lt;filename&gt;</c>, optionally with <c>.compressed</c> appended, and
	///     compression is raw deflate with no zlib header.
	///
	///     Nothing here removes the resolver hook or the resources. Extraction is reported and the
	///     caller decides — an assembly stripped of its dependencies but still carrying a resolver
	///     that looks for them is worse than one left alone, and whether the host is still meant to
	///     run is not this class's call.
	/// </remarks>
	public class CosturaDumper {
		const string PREFIX = "costura.";
		const string COMPRESSED_SUFFIX = ".compressed";

		readonly List<UnpackedFile> files = new List<UnpackedFile>();
		readonly List<EmbeddedResource> resources = new List<EmbeddedResource>();

		/// <summary>The extracted files, decompressed where they needed it.</summary>
		public List<UnpackedFile> Files => files;

		/// <summary>The resources they came from, for a caller that wants to remove them.</summary>
		public List<EmbeddedResource> Resources => resources;

		public bool Detected => files.Count > 0;

		public CosturaDumper(ModuleDefMD module) {
			if (module is not null)
				Find(module);
		}

		void Find(ModuleDefMD module) {
			foreach (var resource in module.Resources) {
				if (resource is not EmbeddedResource embedded)
					continue;
				var name = embedded.Name.String;
				if (name is null || !name.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase))
					continue;

				bool compressed = name.EndsWith(COMPRESSED_SUFFIX, StringComparison.OrdinalIgnoreCase);
				var filename = name.Substring(PREFIX.Length);
				if (compressed)
					filename = filename.Substring(0, filename.Length - COMPRESSED_SUFFIX.Length);
				if (filename.Length == 0)
					continue;

				// Costura also embeds .pdb alongside each assembly. Skipping them is not an oversight:
				// a symbol file is not an assembly, so writing one out through the assembly-file path
				// would produce something no later stage can load.
				if (!filename.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
						!filename.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
					continue;

				byte[] data;
				try {
					data = embedded.CreateReader().ToArray();
					if (compressed)
						data = DeobUtils.Inflate(data, true);	// raw deflate, no zlib header
				}
				catch {
					continue;	// a resource that will not decompress is not one to guess about
				}

				// Check it before claiming it. The name says what Costura intended to store, not what
				// is actually there, and reporting a non-PE as an extracted assembly would send it
				// somewhere that can only fail to load it.
				if (!IsPEFile(data))
					continue;

				files.Add(new UnpackedFile(filename, data));
				resources.Add(embedded);
			}
		}

		static bool IsPEFile(byte[] data) =>
			data is not null && data.Length >= 0x40 && data[0] == (byte)'M' && data[1] == (byte)'Z';
	}
}
