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
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using de4dot.blocks;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4 {
	/// <summary>
	/// Handles .NET Reactor v6.x generic constant decryption methods.
	///
	/// These are &lt;Module&gt; methods with signature !!0 Method&lt;T&gt;(int32), called as
	/// Method&lt;string&gt;(193412188), Method&lt;int[]&gt;(arg), Method&lt;float[]&gt;(arg), etc.
	/// The int32 argument is transformed via multiply-XOR to produce a value whose
	/// top 2 bits select the type (string/scalar/array/default) and bottom 30 bits
	/// give a byte offset into a runtime-initialized byte[].
	///
	/// The type flag → case mapping is shuffled per method (e.g. smethod_1 uses
	/// flag 2 for string, smethod_3 uses flag 3). We detect this by scanning actual
	/// call sites in the module: &lt;string&gt; calls reveal the string flag, &lt;int[]&gt;
	/// calls reveal the array flag, etc.
	/// </summary>
	class GenericConstantDecrypter {
		readonly ModuleDefMD _module;
		byte[] _dataArray;         // runtime-initialized data extracted via dynamic loading
		FieldDef _dataField;       // the byte[] field on <Module> that holds encrypted data
		readonly List<DecrypterMethod> _decrypterMethods = [];

		public class DecrypterMethod {
			public MethodDef method;
			public uint mulConstant;  // per-method multiply constant from IL pattern
			public uint xorConstant;  // per-method XOR constant from IL pattern
			public int stringFlag = -1;  // type flag value for string case
			public int valueFlag = -1;   // type flag value for scalar value case
			public int arrayFlag = -1;   // type flag value for array case
			public int defaultFlag = -1; // type flag value for default(T) case
		}

		public bool Detected => _decrypterMethods.Count > 0;
		public bool Initialized => _dataArray is not null;
		public IEnumerable<DecrypterMethod> DecrypterMethods => _decrypterMethods;

		public GenericConstantDecrypter(ModuleDefMD module) => _module = module;

		/// <summary>
		/// Scans all module types for generic constant decrypter methods matching
		/// the signature: static !!0 Method&lt;T&gt;(int32) with GenericMVar return type.
		/// Extracts the per-method MUL/XOR constants, the shared byte[] field, and
		/// the type flag mapping from the IL switch.
		/// </summary>
		public void Find() {
			foreach (var type in _module.Types) {
				foreach (var method in type.Methods) {
					if (!method.IsStatic || !method.HasBody)
						continue;
					// Must be a generic method with exactly 1 type parameter
					if (!method.HasGenericParameters || method.GenericParameters.Count != 1)
						continue;
					var sig = method.MethodSig;
					if (sig is null || sig.Params.Count != 1)
						continue;
					// Single int32 argument
					if (sig.Params[0].GetElementType() != ElementType.I4)
						continue;
					// Return type is the generic type parameter (!!0)
					if (!(sig.RetType.RemovePinnedAndModifiers() is GenericMVar))
						continue;

					if (TryExtractConstants(method, out uint mul, out uint xor, out var field)) {
						var dm = new DecrypterMethod {
							method = method,
							mulConstant = mul,
							xorConstant = xor,
						};
						_decrypterMethods.Add(dm);
						if (_dataField is null && field is not null)
							_dataField = field;
					}
				}
			}

			if (_decrypterMethods.Count > 0)
				Logger.v("Found {0} generic constant decrypter method(s)", _decrypterMethods.Count);
		}

		public void Initialize(byte[] fileData) {
			if (_dataField is null || _decrypterMethods.Count == 0)
				return;

			// Extraction has to run the target's .cctor, so prefer the out-of-process worker: it fixes
			// the runtime coupling (net10.0's loader rejects Reactor metadata). In-process stays as the
			// fallback for layouts where the worker was not published alongside the host.
			_dataArray = ExtractDataArray(fileData);

			if (_dataArray is null) {
				Logger.w("Could not extract generic constant decrypter data array");
				return;
			}
			
			Logger.v("Generic constant decrypter: data size: {0}", _dataArray.Length);
			DetectTypeFlagsFromCallSites();
		}

		/// <summary>
		///     Extracts via the one-shot net8.0 worker process.
		///
		///     The module is written to a private temp copy rather than handing the worker the user's
		///     original: <c>Initialize</c> receives bytes (which may already be a decrypted image), and a
		///     throwaway copy in the temp directory also keeps a target that touches its own path away
		///     from the input tree. Returns null on any failure so the caller can fall back.
		/// </summary>
		/// <summary>
		///     Obtains the data array, preferring the worker process.
		///
		///     The worker exists because .NET 10's loader rejects Reactor metadata, so in-process
		///     extraction produces nothing when de4dot itself runs on net10.0. If the worker is not
		///     available or fails for any reason, fall back to running the .cctor in-process — that is
		///     what de4dot has always done, and it still works on the .NET Framework build.
		/// </summary>
		byte[] ExtractDataArray(byte[] fileData) {
			var data = TryWorkerExtract(fileData);
			if (data is not null)
				return data;

			return TryDynamicExtract(fileData);
		}

		byte[] TryWorkerExtract(byte[] fileData) {
			string temp = null;
			try {
				temp = Path.Combine(Path.GetTempPath(),
					"de4dot-constdata-" + Guid.NewGuid().ToString("N") + ".dll");
				File.WriteAllBytes(temp, fileData);
				return ConstantDataWorker.TryExtract(temp, _dataField.MDToken.ToInt32());
			}
			catch (Exception ex) {
				Logger.v("Constant-data worker setup failed: {0}: {1}", ex.GetType().Name, ex.Message);
				return null;
			}
			finally {
				if (temp is not null) {
					try { File.Delete(temp); } catch (Exception) { /* temp cleanup is best-effort */ }
				}
			}
		}

		/// <para>
		/// <b>This is the FALLBACK path, used only when the worker cannot be found.</b> .NET 10's
		/// loader validates nested-type metadata more
		/// strictly and rejects Reactor-obfuscated assemblies outright with
		/// <c>BadImageFormatException: Enclosing type(s) not found for type '...'</c>, so the load below
		/// fails, no data array is produced, and every constant and string stays encrypted. Measured:
		/// undecrypted <c>smethod_N</c> call sites went 97 -> 3777 on the corpus when built for net10.0,
		/// while net8.0 extracts a 37636-byte array from the same input. The failure is a verbose-only
		/// log line plus one warning, so it is easy to miss.
		/// </para>
		/// <para>
		/// The real fix is to stop executing the target: read the array from the metadata/RVA data blob
		/// the .cctor initialises it from, instead of loading the assembly and running its .cctor. That
		/// removes the runtime dependency entirely and is also safer, since this currently executes
		/// attacker-controlled static constructors inside de4dot's own process.
		/// </para>
		/// Loads the obfuscated assembly into the current process to trigger its .cctor,
		/// which initializes the byte[] data field. Then reads the field via reflection.
		/// Note: Module.ResolveType(0x02000001) throws for &lt;Module&gt;, so we use
		/// Module.ResolveField() with the field's metadata token directly.
		/// </summary>
		byte[] TryDynamicExtract(byte[] fileData) {
			try {
				var asm = Assembly.Load(fileData);
				var mod = asm.GetModules()[0];

				// For <Module> (global type), use Module.ResolveField() or Module.GetFields()
				// to access module-level fields directly
				try {
					var field = mod.ResolveField(_dataField.MDToken.ToInt32());
					var value = field?.GetValue(null);
					if (value is byte[] { Length: > 0 } bytes)
						return (byte[])bytes.Clone();
				}
				catch (Exception ex) {
					Logger.v("ResolveField failed: {0}", ex.Message);
				}

				// Fallback: enumerate all module-level byte[] fields
				var fields = mod.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
				foreach (var field in fields) {
					if (field.FieldType != typeof(byte[]))
						continue;
					try {
						var value = field.GetValue(null);
						if (value is byte[] { Length: > 0 } bytes)
							return (byte[])bytes.Clone();
					}
					catch {
						// ignored
					}
				}
			}
			catch (Exception ex) {
				Logger.v("Dynamic assembly load failed: {0}: {1}", ex.GetType().Name, ex.Message);
			}
			return null;
		}

		/// <summary>
		/// Extracts MUL and XOR constants from the method's IL pattern:
		///   ldarg.0; ldc.i4 MUL; mul; ldc.i4 XOR; xor; starg
		/// This pattern transforms the int32 argument in-place before using it as
		/// an index into the data array. Also finds the byte[] field (ldsfld).
		/// </summary>
		bool TryExtractConstants(MethodDef method, out uint mul, out uint xor, out FieldDef byteArrayField) {
			mul = 0;
			xor = 0;
			byteArrayField = null;

			var instrs = method.Body.Instructions;

			// Look for: ldarg.0, ldc.i4 <MUL>, mul, ldc.i4 <XOR>, xor, starg.s|starg
			for (int i = 0; i < instrs.Count - 5; i++) {
				var code = instrs[i].OpCode.Code;
				if (code != Code.Ldarg_0 && code != Code.Ldarg && code != Code.Ldarg_S)
					continue;
				if (code is Code.Ldarg or Code.Ldarg_S) {
					if (instrs[i].Operand is not Parameter { Index: 0 } param)
						continue;
				}
				if (!instrs[i + 1].IsLdcI4())
					continue;
				if (instrs[i + 2].OpCode.Code != Code.Mul)
					continue;
				if (!instrs[i + 3].IsLdcI4())
					continue;
				if (instrs[i + 4].OpCode.Code != Code.Xor)
					continue;
				if (instrs[i + 5].OpCode.Code != Code.Starg_S && instrs[i + 5].OpCode.Code != Code.Starg)
					continue;

				mul = (uint)instrs[i + 1].GetLdcI4Value();
				xor = (uint)instrs[i + 3].GetLdcI4Value();

				// Find the byte array field (ldsfld of byte[])
				foreach (var t in instrs)
				{
					if (t.OpCode.Code != Code.Ldsfld)
						continue;
					
					if (t.Operand is FieldDef { FieldType.FullName: "System.Byte[]" } fd) {
						byteArrayField = fd;
						break;
					}
				}

				return true;
			}

			return false;
		}

		/// <summary>
		/// Detects the type flag → case mapping by scanning actual call sites in the module.
		/// For each decrypter method, we find all MethodSpec calls (e.g. Method&lt;string&gt;(123)),
		/// transform the int32 argument with the method's MUL/XOR constants, extract the type
		/// flag (top 2 bits), and classify by the generic type argument:
		///   - System.String → string flag
		///   - SZArray (int[], float[], byte[], etc.) → array flag
		///   - Primitive scalar (int, float, double, etc.) → value flag
		/// The remaining unused flag is assigned as the default flag.
		/// </summary>
		void DetectTypeFlagsFromCallSites() {
			// Build a lookup: MethodDef token → DecrypterMethod
			var tokenToDecrypter = new Dictionary<int, DecrypterMethod>();
			foreach (var dm in _decrypterMethods)
				tokenToDecrypter[dm.method.MDToken.ToInt32()] = dm;

			// Per-method: type flag → category observed
			// category: 0=unknown, 1=string, 2=array, 3=value
			var flagCategories = new Dictionary<DecrypterMethod, Dictionary<int, int>>();
			foreach (var dm in _decrypterMethods)
				flagCategories[dm] = new Dictionary<int, int>();

			// Scan all method bodies for MethodSpec calls to our decrypter methods
			foreach (var type in _module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (!method.HasBody)
						continue;
					var instrs = method.Body.Instructions;
					for (int i = 0; i < instrs.Count; i++) {
						if (instrs[i].OpCode.Code != Code.Call)
							continue;
						if (instrs[i].Operand is not MethodSpec ms)
							continue;

						// Resolve the MethodSpec's underlying method to match our decrypter
						var resolved = (ms.Method as IMethodDefOrRef)?.ResolveMethodDef();
						if (resolved is null)
							continue;
						if (!tokenToDecrypter.TryGetValue(resolved.MDToken.ToInt32(), out var dm))
							continue;

						// Extract the generic type argument
						var gims = ms.GenericInstMethodSig;
						if (gims is null || gims.GenericArguments.Count != 1)
							continue;
						var typeArg = gims.GenericArguments[0];
						int category = ClassifyTypeArg(typeArg);
						if (category == 0)
							continue;

						// Extract the int32 argument (ldc.i4 before the call)
						if (i < 1 || !instrs[i - 1].IsLdcI4())
							continue;
						int arg = instrs[i - 1].GetLdcI4Value();

						// Transform and extract type flag
						uint transformed = unchecked((uint)arg * dm.mulConstant) ^ dm.xorConstant;
						int typeFlag = (int)(transformed >> 30);

						var cats = flagCategories[dm];
						if (!cats.ContainsKey(typeFlag))
							cats[typeFlag] = category;
					}
				}
			}

			// Assign detected flags to each DecrypterMethod
			foreach (var dm in _decrypterMethods) {
				var cats = flagCategories[dm];
				foreach (var kv in cats) {
					switch (kv.Value) {
					case 1: dm.stringFlag = kv.Key; break;
					case 2: dm.arrayFlag = kv.Key; break;
					case 3: dm.valueFlag = kv.Key; break;
					}
				}

				// Infer the remaining flag(s) as default
				InferRemainingFlags(dm);

				if (dm.stringFlag >= 0)
					Logger.v("  Method {0}: str={1} val={2} arr={3} def={4}",
						dm.method.Name, dm.stringFlag, dm.valueFlag, dm.arrayFlag, dm.defaultFlag);
			}
		}

		/// <summary>
		/// Classifies a generic type argument into a category:
		/// 1=string, 2=array (SZArray), 3=scalar value, 0=unknown
		/// </summary>
		static int ClassifyTypeArg(TypeSig typeSig) {
			if (typeSig is null)
				return 0;
			if (typeSig.ElementType == ElementType.String)
				return 1;
			if (typeSig is SZArraySig)
				return 2;
			if (typeSig.ElementType is ElementType.Boolean or ElementType.I1 or ElementType.U1
				or ElementType.I2 or ElementType.U2 or ElementType.Char
				or ElementType.I4 or ElementType.U4
				or ElementType.I8 or ElementType.U8
				or ElementType.R4 or ElementType.R8)
				return 3;
			return 0;
		}

		/// <summary>
		/// After detecting flags from call sites, infer any remaining undetected flags.
		/// If 3 of 4 are known, the 4th is the remaining value.
		/// If only the string flag is known (common case — scalar/array calls may be rare),
		/// we probe the data array to distinguish value/array/default for remaining flags.
		/// </summary>
		void InferRemainingFlags(DecrypterMethod dm) {
			var used = new HashSet<int>();
			if (dm.stringFlag >= 0) used.Add(dm.stringFlag);
			if (dm.valueFlag >= 0) used.Add(dm.valueFlag);
			if (dm.arrayFlag >= 0) used.Add(dm.arrayFlag);
			if (dm.defaultFlag >= 0) used.Add(dm.defaultFlag);

			if (used.Count >= 4)
				return;

			// If we have 3 of 4, assign the remaining flag
			if (used.Count == 3) {
				for (int i = 0; i < 4; i++) {
					if (used.Contains(i))
						continue;
					if (dm.stringFlag < 0) dm.stringFlag = i;
					else if (dm.valueFlag < 0) dm.valueFlag = i;
					else if (dm.arrayFlag < 0) dm.arrayFlag = i;
					else if (dm.defaultFlag < 0) dm.defaultFlag = i;
					break;
				}
				return;
			}

			// For fewer than 3 known flags, try data probing on remaining flags.
			// We scan call sites we've already found (any category) to find sample offsets
			// for each unknown flag, then classify by checking the data layout.
			if (dm.stringFlag < 0)
				return; // Can't do anything without at least the string flag

			// Try to probe the remaining flags using data array heuristics
			ProbeRemainingFlags(dm, used);
		}

		/// <summary>
		/// For each unknown flag value, scans call sites to find a sample argument that
		/// produces that flag, then probes the data array to classify it as value/array/default.
		/// </summary>
		void ProbeRemainingFlags(DecrypterMethod dm, HashSet<int> usedFlags) {
			// Collect sample arguments per type flag from all call sites
			var flagSamples = new Dictionary<int, List<(int arg, TypeSig typeArg)>>();
			for (int f = 0; f < 4; f++) {
				if (!usedFlags.Contains(f))
					flagSamples[f] = [];
			}

			if (flagSamples.Count == 0)
				return;

			// Scan for call sites to this specific method to find sample args for unknown flags
			foreach (var type in _module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (!method.HasBody)
						continue;
					var instrs = method.Body.Instructions;
					for (int i = 1; i < instrs.Count; i++) {
						if (instrs[i].OpCode.Code != Code.Call)
							continue;
						if (instrs[i].Operand is not MethodSpec ms)
							continue;
						var resolved = ms.Method?.ResolveMethodDef();
						if (resolved != dm.method)
							continue;
						if (!instrs[i - 1].IsLdcI4())
							continue;

						int arg = instrs[i - 1].GetLdcI4Value();
						uint transformed = unchecked((uint)arg * dm.mulConstant) ^ dm.xorConstant;
						int typeFlag = (int)(transformed >> 30);

						if (flagSamples.TryGetValue(typeFlag, out var samples)) {
							var gims = ms.GenericInstMethodSig;
							TypeSig typeArg = null;
							if (gims is not null && gims.GenericArguments.Count == 1)
								typeArg = gims.GenericArguments[0];
							samples.Add((arg, typeArg));
						}
					}
				}
			}

			// For flags with samples, classify by data layout
			foreach (var kv in flagSamples) {
				int flag = kv.Key;
				var samples = kv.Value;

				if (samples.Count > 0) {
					// Use the type argument from the first sample to classify
					var typeArg = samples[0].typeArg;
					int cat = ClassifyTypeArg(typeArg);
					if (cat == 2 && dm.arrayFlag < 0) { dm.arrayFlag = flag; usedFlags.Add(flag); continue; }
					if (cat == 3 && dm.valueFlag < 0) { dm.valueFlag = flag; usedFlags.Add(flag); continue; }

					// Fall back to data probing with the first sample
					int arg = samples[0].arg;
					uint transformed = unchecked((uint)arg * dm.mulConstant) ^ dm.xorConstant;
					int offset = (int)((transformed & 0x3FFFFFFFU) << 2);
					var probed = ProbeDataCategory(offset);
					AssignProbedFlag(dm, flag, probed, usedFlags);
				}
			}

			// Any remaining unassigned flags are default
			for (int f = 0; f < 4; f++) {
				if (usedFlags.Contains(f))
					continue;
				if (dm.defaultFlag < 0) {
					dm.defaultFlag = f;
					usedFlags.Add(f);
				}
			}
		}

		/// <summary>
		/// Probes the data array at a given offset to guess the category:
		/// 1=string (valid UTF-8 length prefix), 2=array (valid total+count header), 3=value
		/// </summary>
		int ProbeDataCategory(int offset) {
			if (offset < 0 || offset + 8 > _dataArray.Length)
				return 0;

			// Read first 4 bytes as a potential length
			int len = _dataArray[offset] |
				(_dataArray[offset + 1] << 8) |
				(_dataArray[offset + 2] << 16) |
				(_dataArray[offset + 3] << 24);

			// Check if it could be an array header: totalLen + arrayCount
			int arrayCount = _dataArray[offset + 4] |
				(_dataArray[offset + 5] << 8) |
				(_dataArray[offset + 6] << 16) |
				(_dataArray[offset + 7] << 24);

			// Array: totalLen > 0, arrayCount > 0, totalLen fits in data, and
			// totalLen should be roughly arrayCount * elementSize
			if (len > 0 && arrayCount > 0 && offset + 8 + len <= _dataArray.Length)
				return 2; // likely array

			// String: length prefix + valid UTF-8 data
			if (len is >= 0 and < 10000 && offset + 4 + len <= _dataArray.Length)
				return 1; // likely string

			return 3; // likely scalar value
		}

		void AssignProbedFlag(DecrypterMethod dm, int flag, int category, HashSet<int> usedFlags) {
			switch (category) {
			case 1:
				if (dm.stringFlag < 0) { dm.stringFlag = flag; usedFlags.Add(flag); }
				break;
			case 2:
				if (dm.arrayFlag < 0) { dm.arrayFlag = flag; usedFlags.Add(flag); }
				break;
			case 3:
				if (dm.valueFlag < 0) { dm.valueFlag = flag; usedFlags.Add(flag); }
				break;
			}
		}

		/// <summary>
		/// Decrypts a constant given the method, its int32 argument, and the generic
		/// type argument from the call site.
		/// Transform: (arg * MUL) ^ XOR → top 2 bits = type flag, bottom 30 bits * 4 = offset.
		/// Returns the decoded value as: string, boxed scalar, byte[] (array data), or null (default).
		/// </summary>
		public object DecryptConstant(MethodDef method, MethodSpec gim, int arg) {
			var info = FindDecrypterMethod(method);
			if (info is null || _dataArray is null)
				return null;

			uint transformed = unchecked((uint)arg * info.mulConstant) ^ info.xorConstant;
			int typeFlag = (int)(transformed >> 30);
			int offset = (int)((transformed & 0x3FFFFFFFU) << 2);

			if (typeFlag == info.stringFlag)
				return DecryptString(offset);
			if (typeFlag == info.valueFlag)
				return DecryptValue(offset, gim);
			if (typeFlag == info.arrayFlag)
				return DecryptArray(offset, gim);
			// default flag or unknown → default(T)
			return null;
		}

		/// <summary>
		/// Legacy method for string-only decryption. Used by the string inliner.
		/// </summary>
		public string Decrypt(MethodDef method, int arg) {
			var info = FindDecrypterMethod(method);
			if (info is null || _dataArray is null)
				return null;

			uint transformed = unchecked((uint)arg * info.mulConstant) ^ info.xorConstant;
			int offset = (int)((transformed & 0x3FFFFFFFU) << 2);
			return DecryptString(offset);
		}

		string DecryptString(int offset) {
			if (offset < 0 || offset + 4 > _dataArray.Length)
				return null;

			int length = _dataArray[offset] |
				(_dataArray[offset + 1] << 8) |
				(_dataArray[offset + 2] << 16) |
				(_dataArray[offset + 3] << 24);
			if (length < 0 || offset + 4 + length > _dataArray.Length)
				return null;
			return Encoding.UTF8.GetString(_dataArray, offset + 4, length);
		}

		object DecryptValue(int offset, MethodSpec gim) {
			// Scalar value: raw bytes at offset, size determined by the type argument
			var elementType = GetElementTypeFromGim(gim);
			int size = GetElementSize(elementType);
			if (size <= 0 || offset < 0 || offset + size > _dataArray.Length)
				return null;

			return elementType switch {
				ElementType.Boolean => _dataArray[offset] != 0,
				ElementType.I1 => (sbyte)_dataArray[offset],
				ElementType.U1 => _dataArray[offset],
				ElementType.I2 => (short)(_dataArray[offset] | (_dataArray[offset + 1] << 8)),
				ElementType.U2 => (ushort)(_dataArray[offset] | (_dataArray[offset + 1] << 8)),
				ElementType.Char => (char)(_dataArray[offset] | (_dataArray[offset + 1] << 8)),
				ElementType.I4 => _dataArray[offset] | (_dataArray[offset + 1] << 8) | (_dataArray[offset + 2] << 16) |
				                  (_dataArray[offset + 3] << 24),
				ElementType.U4 => (uint)(_dataArray[offset] | (_dataArray[offset + 1] << 8) |
				                         (_dataArray[offset + 2] << 16) | (_dataArray[offset + 3] << 24)),
				ElementType.I8 => BitConverter.ToInt64(_dataArray, offset),
				ElementType.U8 => BitConverter.ToUInt64(_dataArray, offset),
				ElementType.R4 => BitConverter.ToSingle(_dataArray, offset),
				ElementType.R8 => BitConverter.ToDouble(_dataArray, offset),
				_ => null
			};
		}

		/// <summary>
		/// Decrypts an array constant. The data layout at offset is:
		///   4 bytes: total data length (bytes)
		///   4 bytes: array element count
		///   N bytes: raw array data
		/// Returns an ArrayConstant with the element type info and raw bytes,
		/// which the inliner can use with InitializedDataCreator.
		/// </summary>
		object DecryptArray(int offset, MethodSpec gim) {
			if (offset < 0 || offset + 8 > _dataArray.Length)
				return null;

			int totalLen = _dataArray[offset] |
				(_dataArray[offset + 1] << 8) |
				(_dataArray[offset + 2] << 16) |
				(_dataArray[offset + 3] << 24);
			int arrayLen = _dataArray[offset + 4] |
				(_dataArray[offset + 5] << 8) |
				(_dataArray[offset + 6] << 16) |
				(_dataArray[offset + 7] << 24);

			if (totalLen < 0 || offset + 8 + totalLen > _dataArray.Length)
				return null;
			if (arrayLen < 0)
				return null;

			var rawData = new byte[totalLen];
			Array.Copy(_dataArray, offset + 8, rawData, 0, totalLen);

			// Get the element type from the generic instantiation (e.g. int[] → int)
			var arrayElementType = GetArrayElementTypeFromGim(gim);
			return new ArrayConstant(rawData, arrayElementType);
		}

		ElementType GetElementTypeFromGim(MethodSpec gim) {
			var gims = gim?.GenericInstMethodSig;
			if (gims?.GenericArguments.Count is not 1)
				return ElementType.End;
			return gims.GenericArguments[0].ElementType;
		}

		/// <summary>
		/// For array types like int[], float[], etc., returns the SZArray's element type.
		/// </summary>
		TypeSig GetArrayElementTypeFromGim(MethodSpec gim) {
			var gims = gim?.GenericInstMethodSig;
			if (gims is null || gims.GenericArguments.Count != 1)
				return null;
			var typeSig = gims.GenericArguments[0];
			if (typeSig is SZArraySig szArray)
				return szArray.Next;
			return null;
		}

		static int GetElementSize(ElementType et) =>
			et switch {
				ElementType.Boolean or ElementType.I1 or ElementType.U1 => 1,
				ElementType.Char or ElementType.I2 or ElementType.U2 => 2,
				ElementType.I4 or ElementType.U4 or ElementType.R4 => 4,
				ElementType.I8 or ElementType.U8 or ElementType.R8 => 8,
				_ => -1
			};

		DecrypterMethod FindDecrypterMethod(MethodDef method) => _decrypterMethods.FirstOrDefault(dm => dm.method == method);
	}

	/// <summary>
	/// Holds raw array data and element type info for inlining array constants.
	/// </summary>
	class ArrayConstant {
		public byte[] Data { get; }
		public TypeSig ElementType { get; }

		public ArrayConstant(byte[] data, TypeSig elementType) {
			Data = data;
			ElementType = elementType;
		}
	}
}
