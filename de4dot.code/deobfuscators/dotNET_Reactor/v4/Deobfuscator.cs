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
using System.Linq;
using System.Text.RegularExpressions;
using dnlib.PE;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using de4dot.blocks;
using de4dot.blocks.cflow;

namespace de4dot.code.deobfuscators.dotNET_Reactor.v4 {
	public class DeobfuscatorInfo : DeobfuscatorInfoBase {
		public const string THE_NAME = ".NET Reactor";
		public const string THE_TYPE = "dr4";
		public const string SHORT_NAME_REGEX = @"!^[A-Za-z0-9]{2,3}$";
		const string DEFAULT_REGEX = DeobfuscatorBase.DEFAULT_ASIAN_VALID_NAME_REGEX;
		BoolOption decryptMethods;
		BoolOption decryptBools;
		BoolOption restoreTypes;
		BoolOption inlineMethods;
		BoolOption removeInlinedMethods;
		BoolOption dumpEmbeddedAssemblies;
		BoolOption decryptResources;
		BoolOption removeNamespaces;
		BoolOption removeAntiStrongName;
		BoolOption renameShort;

		public DeobfuscatorInfo()
			: base(DEFAULT_REGEX) {
			decryptMethods = new BoolOption(null, MakeArgName("methods"), "Decrypt methods", true);
			decryptBools = new BoolOption(null, MakeArgName("bools"), "Decrypt booleans", true);
			restoreTypes = new BoolOption(null, MakeArgName("types"), "Restore types (object -> real type)", true);
			inlineMethods = new BoolOption(null, MakeArgName("inline"), "Inline short methods", true);
			removeInlinedMethods = new BoolOption(null, MakeArgName("remove-inlined"), "Remove inlined methods", true);
			dumpEmbeddedAssemblies = new BoolOption(null, MakeArgName("embedded"), "Dump embedded assemblies", true);
			decryptResources = new BoolOption(null, MakeArgName("rsrc"), "Decrypt resources", true);
			removeNamespaces = new BoolOption(null, MakeArgName("ns1"), "Clear namespace if there's only one class in it", true);
			removeAntiStrongName = new BoolOption(null, MakeArgName("sn"), "Remove anti strong name code", true);
			renameShort = new BoolOption(null, MakeArgName("sname"), "Rename short names", false);
		}

		public override string Name => THE_NAME;
		public override string Type => THE_TYPE;

		public override IDeobfuscator CreateDeobfuscator() =>
			new Deobfuscator(new Deobfuscator.Options {
				ValidNameRegex = validNameRegex.Get(),
				DecryptMethods = decryptMethods.Get(),
				DecryptBools = decryptBools.Get(),
				RestoreTypes = restoreTypes.Get(),
				InlineMethods = inlineMethods.Get(),
				RemoveInlinedMethods = removeInlinedMethods.Get(),
				DumpEmbeddedAssemblies = dumpEmbeddedAssemblies.Get(),
				DecryptResources = decryptResources.Get(),
				RemoveNamespaces = removeNamespaces.Get(),
				RemoveAntiStrongName = removeAntiStrongName.Get(),
				RenameShort = renameShort.Get(),
			});

		protected override IEnumerable<Option> GetOptionsInternal() =>
			new List<Option>() {
				decryptMethods,
				decryptBools,
				restoreTypes,
				inlineMethods,
				removeInlinedMethods,
				dumpEmbeddedAssemblies,
				decryptResources,
				removeNamespaces,
				removeAntiStrongName,
				renameShort,
			};
	}

	sealed class Deobfuscator : DeobfuscatorBase {
		Options options;
		string obfuscatorName = DeobfuscatorInfo.THE_NAME;

		MyPEImage peImage;
		byte[] fileData;
		MethodsDecrypter methodsDecrypter;
		StringDecrypter stringDecrypter;
		GenericConstantDecrypter genericConstantDecrypter;
		GenericConstantInliner genericConstantInliner;
		BooleanDecrypter booleanDecrypter;
		BooleanValueInliner booleanValueInliner;
		MetadataTokenObfuscator metadataTokenObfuscator;
		AssemblyResolver assemblyResolver;
		ResourceResolver resourceResolver;
		AntiStrongName antiStrongname;
		EmptyClass emptyClass;
		ProxyCallFixer proxyCallFixer;

		bool unpackedNativeFile = false;
		bool canRemoveDecrypterType = true;
		bool startedDeobfuscating = false;

		internal class Options : OptionsBase {
			public bool DecryptMethods { get; set; }
			public bool DecryptBools { get; set; }
			public bool RestoreTypes { get; set; }
			public bool InlineMethods { get; set; }
			public bool RemoveInlinedMethods { get; set; }
			public bool DumpEmbeddedAssemblies { get; set; }
			public bool DecryptResources { get; set; }
			public bool RemoveNamespaces { get; set; }
			public bool RemoveAntiStrongName { get; set; }
			public bool RenameShort { get; set; }
		}

		public override string Type => DeobfuscatorInfo.THE_TYPE;
		public override string TypeLong => DeobfuscatorInfo.THE_NAME + " 4.x";
		public override string Name => obfuscatorName;
		protected override bool CanInlineMethods => !startedDeobfuscating || options.InlineMethods;

		/// <summary>
		/// Returns per-method block deobfuscators run during control flow deobfuscation.
		/// Order matters: DotNetReactorCflowDeobfuscator (existing v4 patterns) runs first,
		/// then XorSwitchDeobfuscator (v6.x XOR-keyed switch state machines), then
		/// MethodCallInliner (inline small helper methods after cflow is resolved).
		/// </summary>
		public override IEnumerable<IBlocksDeobfuscator> BlocksDeobfuscators =>
			CanInlineMethods ? [
				new DotNetReactorCflowDeobfuscator(),
				new xorswitch.XorSwitchDeobfuscator(),
				new MethodCallInliner(false)
			] : [];

		public Deobfuscator(Options options)
			: base(options) {
			this.options = options;

			if (options.RemoveNamespaces)
				RenamingOptions |= RenamingOptions.RemoveNamespaceIfOneType;
			else
				RenamingOptions &= ~RenamingOptions.RemoveNamespaceIfOneType;
			if (options.RenameShort)
				options.ValidNameRegex.Regexes.Insert(0, new NameRegex(DeobfuscatorInfo.SHORT_NAME_REGEX));
		}

		public override byte[] UnpackNativeFile(IPEImage peImage) {
			var data = new NativeImageUnpacker(peImage).Unpack();
			if (data == null)
				return null;

			unpackedNativeFile = true;
			ModuleBytes = data;
			return data;
		}

		static Regex isRandomName = new Regex(@"^[A-Z]{30,40}$");
		static Regex isRandomNameMembers = new Regex(@"^(?:[a-zA-Z0-9]{9,11}|[a-zA-Z0-9]{18,20})$");	// methods, fields, props, events
		static Regex isRandomNameTypes = new Regex(@"^[a-zA-Z0-9]{18,20}(?:`\d+)?$");	// types, namespaces

		bool CheckValidName(string name, Regex regex) {
			if (isRandomName.IsMatch(name))
				return false;
			if (regex.IsMatch(name)) {
				if (RandomNameChecker.IsRandom(name))
					return false;
				if (!RandomNameChecker.IsNonRandom(name))
					return false;
			}
			return CheckValidName(name);
		}

		public override bool IsValidNamespaceName(string ns) {
			if (ns == null)
				return false;
			if (ns.Contains('.'))
				return base.IsValidNamespaceName(ns);
			return CheckValidName(ns, isRandomNameTypes);
		}

		public override bool IsValidTypeName(string name) => name != null && CheckValidName(name, isRandomNameTypes);
		public override bool IsValidMethodName(string name) => name != null && CheckValidName(name, isRandomNameMembers);
		public override bool IsValidPropertyName(string name) => name != null && CheckValidName(name, isRandomNameMembers);
		public override bool IsValidEventName(string name) => name != null && CheckValidName(name, isRandomNameMembers);
		public override bool IsValidFieldName(string name) => name != null && CheckValidName(name, isRandomNameMembers);
		public override bool IsValidGenericParamName(string name) => name != null && CheckValidName(name, isRandomNameMembers);
		public override bool IsValidMethodArgName(string name) => name != null && CheckValidName(name, isRandomNameMembers);
		public override bool IsValidMethodReturnArgName(string name) => string.IsNullOrEmpty(name) || CheckValidName(name, isRandomNameMembers);
		public override bool IsValidResourceKeyName(string name) => name != null && CheckValidName(name, isRandomNameMembers);

		protected override int DetectInternal() {
			int val = 0;

			int sum = ToInt32(methodsDecrypter.Detected) +
					ToInt32(stringDecrypter.Detected) +
					ToInt32(booleanDecrypter.Detected) +
					ToInt32(assemblyResolver.Detected) +
					ToInt32(resourceResolver.Detected);
			if (sum > 0)
				val += 100 + 10 * (sum - 1);

			if (sum == 0) {
				if (HasMetadataStream("#GUlD") && HasMetadataStream("#Blop"))
					val += 10;
			}

			return val;
		}

		protected override void ScanForObfuscator() {
			methodsDecrypter = new MethodsDecrypter(module);
			methodsDecrypter.Find();
			proxyCallFixer = new ProxyCallFixer(module, DeobfuscatedFile);
			proxyCallFixer.FindDelegateCreator(module);
			stringDecrypter = new StringDecrypter(module);
			stringDecrypter.Find(DeobfuscatedFile);
			// Scan for v6.x generic constant decrypter methods: !!0 Method<T>(int32)
			genericConstantDecrypter = new GenericConstantDecrypter(module);
			genericConstantDecrypter.Find();
			booleanDecrypter = new BooleanDecrypter(module);
			booleanDecrypter.Find();
			assemblyResolver = new AssemblyResolver(module);
			assemblyResolver.Find(DeobfuscatedFile);
			obfuscatorName = DetectVersion();
			if (unpackedNativeFile)
				obfuscatorName += " (native)";
			resourceResolver = new ResourceResolver(module);
			resourceResolver.Find(DeobfuscatedFile);
		}

		string DetectVersion() {
			/*
			Methods decrypter locals (not showing its own types):
			3.7.0.3:
					"System.Byte[]"
					"System.Int32"
					"System.Int32[]"
					"System.IntPtr"
					"System.IO.BinaryReader"
					"System.IO.MemoryStream"
					"System.Object"
					"System.Reflection.Assembly"
					"System.Security.Cryptography.CryptoStream"
					"System.Security.Cryptography.ICryptoTransform"
					"System.Security.Cryptography.RijndaelManaged"
					"System.String"

			3.9.8.0:
			-		"System.Int32[]"
			+		"System.Diagnostics.StackFrame"

			4.0.0.0: (jitter)
			-		"System.Diagnostics.StackFrame"
			-		"System.Object"
			+		"System.Boolean"
			+		"System.Collections.IEnumerator"
			+		"System.Delegate"
			+		"System.Diagnostics.Process"
			+		"System.Diagnostics.ProcessModule"
			+		"System.Diagnostics.ProcessModuleCollection"
			+		"System.IDisposable"
			+		"System.Int64"
			+		"System.UInt32"
			+		"System.UInt64"

			4.1.0.0: (jitter)
			+		"System.Reflection.Assembly"

			4.3.1.0: (jitter)
			+		"System.Byte&"
			*/

			LocalTypes localTypes;
			int minVer = -1;
			foreach (var info in stringDecrypter.DecrypterInfos) {
				if (info.key == null)
					continue;
				localTypes = new LocalTypes(info.method);
				if (!localTypes.Exists("System.IntPtr"))
					return DeobfuscatorInfo.THE_NAME + " <= 3.7";
				minVer = 3800;
				break;
			}

			if (methodsDecrypter.DecrypterTypeVersion != DnrDecrypterType.V1)
				return DeobfuscatorInfo.THE_NAME;

			if (methodsDecrypter.Method == null) {
				if (minVer >= 3800)
					return DeobfuscatorInfo.THE_NAME + " >= 3.8";
				return DeobfuscatorInfo.THE_NAME;
			}
			localTypes = new LocalTypes(methodsDecrypter.Method);

			if (localTypes.Exists("System.Int32[]")) {
				if (minVer >= 3800)
					return DeobfuscatorInfo.THE_NAME + " 3.8.4.1 - 3.9.0.1";
				return DeobfuscatorInfo.THE_NAME + " <= 3.9.0.1";
			}
			if (!localTypes.Exists("System.Diagnostics.Process")) {	// If < 4.0
				if (localTypes.Exists("System.Diagnostics.StackFrame"))
					return DeobfuscatorInfo.THE_NAME + " 3.9.8.0";
			}

			var compileMethod = MethodsDecrypter.FindDnrCompileMethod(methodsDecrypter.Method.DeclaringType);
			if (compileMethod == null) {
				DeobfuscatedFile.Deobfuscate(methodsDecrypter.Method);
				if (!MethodsDecrypter.IsNewer45Decryption(methodsDecrypter.Method))
					return DeobfuscatorInfo.THE_NAME + " < 4.0";
				return DeobfuscatorInfo.THE_NAME + " 4.5+";
			}
			DeobfuscatedFile.Deobfuscate(compileMethod);
			bool compileMethodHasConstant_0x70000000 = DeobUtils.HasInteger(compileMethod, 0x70000000);	// 4.0-4.1
			DeobfuscatedFile.Deobfuscate(methodsDecrypter.Method);
			bool hasCorEnableProfilingString = FindString(methodsDecrypter.Method, "Cor_Enable_Profiling");	// 4.1-4.4
			bool hasCatchString = FindString(methodsDecrypter.Method, "catch: ");	// <= 4.7

			if (compileMethodHasConstant_0x70000000) {
				if (hasCorEnableProfilingString)
					return DeobfuscatorInfo.THE_NAME + " 4.1";
				return DeobfuscatorInfo.THE_NAME + " 4.0";
			}
			if (!hasCorEnableProfilingString) {
				bool callsReverse = DotNetUtils.CallsMethod(methodsDecrypter.Method, "System.Void System.Array::Reverse(System.Array)");
				if (!callsReverse)
					return DeobfuscatorInfo.THE_NAME + " 4.0 - 4.4";

				int numIntPtrSizeCompares = CountCompareSystemIntPtrSize(methodsDecrypter.Method);
				bool hasSymmetricAlgorithm = new LocalTypes(methodsDecrypter.Method).Exists("System.Security.Cryptography.SymmetricAlgorithm");
				if (module.IsClr40) {
					switch (numIntPtrSizeCompares) {
					case 7:
					case 9: return DeobfuscatorInfo.THE_NAME + " 4.5";
					case 10:
						if (!hasSymmetricAlgorithm)
							return DeobfuscatorInfo.THE_NAME + " 4.6";
						if (hasCatchString)
							return DeobfuscatorInfo.THE_NAME + " 4.7";
						return DeobfuscatorInfo.THE_NAME + " 4.8";
					}
				}
				else {
					switch (numIntPtrSizeCompares) {
					case 6:
					case 8: return DeobfuscatorInfo.THE_NAME + " 4.5";
					case 9:
						if (!hasSymmetricAlgorithm)
							return DeobfuscatorInfo.THE_NAME + " 4.6";
						if (hasCatchString)
							return DeobfuscatorInfo.THE_NAME + " 4.7";
						return DeobfuscatorInfo.THE_NAME + " 4.8";
					}
				}

				// Should never be reached unless it's a new version
				return DeobfuscatorInfo.THE_NAME + " 4.5+";
			}

			// 4.2-4.4

			if (!localTypes.Exists("System.Byte&"))
				return DeobfuscatorInfo.THE_NAME + " 4.2";

			localTypes = new LocalTypes(compileMethod);
			if (localTypes.Exists("System.Object"))
				return DeobfuscatorInfo.THE_NAME + " 4.4";
			return DeobfuscatorInfo.THE_NAME + " 4.3";
		}

		static int CountCompareSystemIntPtrSize(MethodDef method) {
			if (method == null || method.Body == null)
				return 0;
			int count = 0;
			var instrs = method.Body.Instructions;
			for (int i = 1; i < instrs.Count - 1; i++) {
				var ldci4 = instrs[i];
				if (!ldci4.IsLdcI4() || ldci4.GetLdcI4Value() != 4)
					continue;
				if (!instrs[i + 1].IsConditionalBranch())
					continue;
				var call = instrs[i - 1];
				if (call.OpCode.Code != Code.Call)
					continue;
				var calledMethod = call.Operand as MemberRef;
				if (calledMethod is not { FullName: "System.Int32 System.IntPtr::get_Size()" })
					continue;

				count++;
			}
			return count;
		}

		static bool FindString(MethodDef method, string s) => DotNetUtils.GetCodeStrings(method).Any(cs => cs == s);

		public override bool GetDecryptedModule(int count, ref byte[] newFileData, ref DumpedMethods dumpedMethods) {
			if (count != 0)
				return false;
			fileData = ModuleBytes ?? DeobUtils.ReadModule(module);
			peImage = new MyPEImage(fileData);

			if (!options.DecryptMethods)
				return false;

			var tokenToNativeCode = new Dictionary<uint,byte[]>();
			if (!methodsDecrypter.Decrypt(peImage, DeobfuscatedFile, ref dumpedMethods, tokenToNativeCode, unpackedNativeFile))
				return false;

			newFileData = fileData;
			return true;
		}

		public override IDeobfuscator ModuleReloaded(ModuleDefMD module) {
			FreePEImage();
			var newOne = new Deobfuscator(options);
			newOne.SetModule(module);
			newOne.fileData = fileData;
			newOne.peImage = new MyPEImage(fileData);
			newOne.methodsDecrypter = new MethodsDecrypter(module, methodsDecrypter);
			newOne.proxyCallFixer = new ProxyCallFixer(module, proxyCallFixer);
			newOne.stringDecrypter = new StringDecrypter(module, stringDecrypter);
			// Re-detect generic decrypter methods in the reloaded module (new metadata tokens)
			newOne.genericConstantDecrypter = new GenericConstantDecrypter(module);
			newOne.genericConstantDecrypter.Find();
			newOne.booleanDecrypter = new BooleanDecrypter(module, booleanDecrypter);
			newOne.assemblyResolver = new AssemblyResolver(module, assemblyResolver);
			newOne.resourceResolver = new ResourceResolver(module, resourceResolver);
			newOne.methodsDecrypter.Reloaded();
			return newOne;
		}

		void FreePEImage() {
			peImage?.Dispose();
			peImage = null;
		}

		void RemoveMethods() {
			if (methodsDecrypter.Method != null) {
				AddEntryPointCallToBeRemoved(methodsDecrypter.Method);
			}

			MethodDef antiTamper = null;
			MethodDef antiDebug = null;

			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (!method.HasBody) continue;

					foreach (var str in DotNetUtils.GetCodeStrings(method)) {
						if (str == " is tampered.") {
							antiTamper = method;
							AddMethodToBeRemoved(antiTamper, "Anti tamper method");
							break;
						}

						if (str == "Debugger Detected") {
							antiDebug = method;
							AddMethodToBeRemoved(antiDebug, "Anti debug method");
							break;
						}
					}
				}
			}

			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (!method.HasBody) continue;
					if (emptyClass?.Method != null)
						AddCallToBeRemoved(method, emptyClass.Method);

					if (antiTamper != null)
						AddCallToBeRemoved(method, antiTamper);

					if (antiDebug != null)
						AddCallToBeRemoved(method, antiDebug);

					if (methodsDecrypter.Method != null)
						AddCallToBeRemoved(method, methodsDecrypter.Method);

					// AntiILDASM
					var instr = method.Body.Instructions;

					for (int i = 0; i < instr.Count; i++) {
						if (instr[i].OpCode == OpCodes.Call && instr[i].Operand is null && instr[i + 1].IsStloc() && instr[i + 2].IsLdloc()) {
							instr[i].OpCode = OpCodes.Nop; // v6.0.0.0
							instr[i + 2].OpCode = OpCodes.Nop;
						}
						if (instr[i].OpCode == OpCodes.Call && instr[i].Operand is null) {
							instr[i].OpCode = OpCodes.Nop;
						}
					}
				}
			}
		}

		public override void DeobfuscateBegin() {
			base.DeobfuscateBegin();

			proxyCallFixer.Initialize();
			proxyCallFixer.Find();
			proxyCallFixer.DeobfuscateAll();

			// Initialize generic constant decrypter AFTER proxy call resolution (proxyCallFixer above).
			// Uses Assembly.Load() to dynamically load the assembly, triggering the .cctor which
			// initializes the runtime byte[] data field via XorShift PRNG + block XOR decryption.
			// The field value is then read via reflection (Module.ResolveField).
			if (genericConstantDecrypter.Detected)
				genericConstantDecrypter.Initialize(fileData ?? DeobUtils.ReadModule(module));

			var cflowInliner = new CflowConstantsInliner(module, DeobfuscatedFile);
			cflowInliner.InlineAllConstants();
			AddTypeToBeRemoved(cflowInliner.Type, "Cflow constants type");

			bool decryptStrings = Operations.DecryptStrings == OpDecryptString.Static;
			if (decryptStrings)
				stringDecrypter.Initialize(peImage, fileData, DeobfuscatedFile, ref decryptStrings);
			if (!stringDecrypter.Detected || !decryptStrings)
				FreePEImage();
			booleanDecrypter.Initialize(fileData, DeobfuscatedFile);
			booleanValueInliner = new BooleanValueInliner();
			emptyClass = new EmptyClass(module);

			if (options.DecryptBools) {
				booleanValueInliner.Add(booleanDecrypter.Method, (_, _, args) => booleanDecrypter.Decrypt((int)args[0]));
			}

			if (decryptStrings) {
				foreach (var info in stringDecrypter.DecrypterInfos) {
					staticStringInliner.Add(info.method, (method2, _, args) => stringDecrypter.Decrypt(method2, (int)args[0]));
				}
				if (stringDecrypter.OtherStringDecrypter != null) {
					staticStringInliner.Add(stringDecrypter.OtherStringDecrypter, (_, _, args) => stringDecrypter.Decrypt((string)args[0]));
				}
			}
			// Register generic decrypter methods with both string and constant inliners.
			// Method<string>(...) calls are handled by the string inliner; non-string calls
			// (Method<int[]>(...), Method<float[]>(...), etc.) by the constant inliner.
			if (genericConstantDecrypter.Initialized) {
				genericConstantInliner = new GenericConstantInliner(module, initializedDataCreator);
				foreach (var dm in genericConstantDecrypter.DecrypterMethods) {
					staticStringInliner.Add(dm.method, (method2, _, args) => genericConstantDecrypter.Decrypt(method2, (int)args[0]));
					genericConstantInliner.Add(dm.method, (method2, gim, args) => genericConstantDecrypter.DecryptConstant(method2, gim, (int)args[0]));
				}
			}
			DeobfuscatedFile.StringDecryptersAdded();

			metadataTokenObfuscator = new MetadataTokenObfuscator(module);
			antiStrongname = new AntiStrongName(GetDecrypterType());

			bool removeResourceResolver = false;
			if (options.DecryptResources) {
				resourceResolver.Initialize(DeobfuscatedFile, this);
				DecryptResources();
				if (options.InlineMethods) {
					AddTypeToBeRemoved(resourceResolver.Type, "Resource decrypter type");
					removeResourceResolver = true;
				}
				AddEntryPointCallToBeRemoved(resourceResolver.InitMethod);
				AddCctorInitCallToBeRemoved(resourceResolver.InitMethod);
			}
			if (resourceResolver.Detected && !removeResourceResolver && !resourceResolver.FoundResource)
				canRemoveDecrypterType = false;	// There may be calls to its .ctor

			//if (Operations.DecryptStrings != OpDecryptString.None)
			//	AddResourceToBeRemoved(stringDecrypter.Resource, "Encrypted strings");
			//else
			//	canRemoveDecrypterType = false;

			if (options.DecryptMethods && !methodsDecrypter.HasNativeMethods) {
				AddResourceToBeRemoved(methodsDecrypter.Resource, "Encrypted methods");
				AddCctorInitCallToBeRemoved(methodsDecrypter.Method);
			}
			else
				canRemoveDecrypterType = false;

			if (options.DecryptBools)
				AddResourceToBeRemoved(booleanDecrypter.Resource, "Encrypted booleans");
			else
				canRemoveDecrypterType = false;

			if (!options.RemoveAntiStrongName)
				canRemoveDecrypterType = false;

			// The inlined methods may contain calls to the decrypter class
			if (!options.InlineMethods)
				canRemoveDecrypterType = false;

			if (options.DumpEmbeddedAssemblies) {
				if (options.InlineMethods)
					AddTypeToBeRemoved(assemblyResolver.Type, "Assembly resolver");
				AddEntryPointCallToBeRemoved(assemblyResolver.InitMethod);
				AddCctorInitCallToBeRemoved(assemblyResolver.InitMethod);
				DumpEmbeddedAssemblies();
			}

			if (options.InlineMethods)
				AddTypeToBeRemoved(metadataTokenObfuscator.Type, "Metadata token obfuscator");

			AddCctorInitCallToBeRemoved(emptyClass.Method);
			AddCtorInitCallToBeRemoved(emptyClass.Method);
			AddEntryPointCallToBeRemoved(emptyClass.Method);
			if (options.InlineMethods)
				AddTypeToBeRemoved(emptyClass.Type, "Empty class");

			RemoveMethods();

			startedDeobfuscating = true;
		}

		void AddEntryPointCallToBeRemoved(MethodDef methodToBeRemoved) {
			var entryPoint = module.EntryPoint;
			AddCallToBeRemoved(entryPoint, methodToBeRemoved);
			foreach (var calledMethod in DotNetUtils.GetCalledMethods(module, entryPoint))
				AddCallToBeRemoved(calledMethod, methodToBeRemoved);
		}

		void DecryptResources() {
			var rsrc = resourceResolver.MergeResources();
			if (rsrc == null)
				return;
			AddResourceToBeRemoved(rsrc, "Encrypted resources");
		}

		void DumpEmbeddedAssemblies() {
			if (!options.DumpEmbeddedAssemblies)
				return;
			foreach (var info in assemblyResolver.GetEmbeddedAssemblies(DeobfuscatedFile, this)) {
				var simpleName = Utils.GetAssemblySimpleName(info.name);
				DeobfuscatedFile.CreateAssemblyFile(info.resource.CreateReader().ToArray(), simpleName, null);
				AddResourceToBeRemoved(info.resource, $"Embedded assembly: {info.name}");
			}
		}

		public override bool DeobfuscateOther(Blocks blocks) => booleanValueInliner.Decrypt(blocks) > 0;

		public override void DeobfuscateMethodBegin(Blocks blocks) {
			proxyCallFixer.Deobfuscate(blocks);
		}

		public override void DeobfuscateMethodEnd(Blocks blocks) {
			metadataTokenObfuscator.Deobfuscate(blocks);
			FixTypeofDecrypterInstructions(blocks);
			RemoveAntiStrongNameCode(blocks);
			base.DeobfuscateMethodEnd(blocks);
		}

		void RemoveAntiStrongNameCode(Blocks blocks) {
			if (!options.RemoveAntiStrongName)
				return;
			if (antiStrongname.Remove(blocks))
				Logger.v("Removed anti strong name code");
		}

		TypeDef GetDecrypterType() => methodsDecrypter.DecrypterType ?? stringDecrypter.DecrypterType ?? booleanDecrypter.DecrypterType;

		void FixTypeofDecrypterInstructions(Blocks blocks) {
			var type = GetDecrypterType();
			if (type == null)
				return;

			foreach (var block in blocks.MethodBlocks.GetAllBlocks()) {
				var instructions = block.Instructions;
				for (int i = 0; i < instructions.Count; i++) {
					var instr = instructions[i];
					if (instr.OpCode.Code != Code.Ldtoken)
						continue;
					if (!new SigComparer().Equals(type, instr.Operand as ITypeDefOrRef))
						continue;
					instructions[i] = new Instr(OpCodes.Ldtoken.ToInstruction(blocks.Method.DeclaringType));
				}
			}
		}

		public override void DeobfuscateEnd() {
			FreePEImage();
			RemoveProxyDelegates(proxyCallFixer, false);
			RemoveInlinedMethods();
			if (options.RestoreTypes)
				new TypesRestorer(module).Deobfuscate();

			var decrypterType = GetDecrypterType();
			if (canRemoveDecrypterType && IsTypeCalled(decrypterType))
				canRemoveDecrypterType = false;

			if (canRemoveDecrypterType)
				AddTypeToBeRemoved(decrypterType, "Decrypter type");
			else
				Logger.v("Could not remove decrypter type");

			FixEntryPoint();
			CleanDisplayClasses();
			FixFakeInstanceStubs();

			base.DeobfuscateEnd();
		}

		// Must run after ProxyCallFixer: the fake-instance stubs only become recognizable once their
		// proxy dispatcher call has been resolved back to the real target method.
		void FixFakeInstanceStubs() {
			var fixer = new FakeInstanceStubFixer(module);
			int count = fixer.Fix();
			if (count > 0) {
				Logger.v("Converted {0} fake-instance proxy stub(s) to static", count);
				foreach (var method in fixer.FixedMethods)
					Logger.v("  {0}", Utils.RemoveNewlines(method));
			}
		}

		void CleanDisplayClasses() {
			var cleaner = new DisplayClassCleaner(module);
			cleaner.Find();
			AddFieldsToBeRemoved(cleaner.FieldsToRemove, "Reactor-injected DisplayClass static field");
			AddMethodsToBeRemoved(cleaner.MethodsToRemove, "Reactor-injected DisplayClass null-check method");
		}

		void FixEntryPoint() {
			if (!module.IsClr1x)
				return;

			var ep = module.EntryPoint;
			if (ep == null)
				return;
			if (ep.MethodSig.GetParamCount() <= 1)
				return;

			ep.MethodSig = MethodSig.CreateStatic(ep.MethodSig.RetType, new SZArraySig(module.CorLibTypes.String));
			ep.ParamDefs.Clear();
			ep.Parameters.UpdateParameterTypes();
		}

		void RemoveInlinedMethods() {
			if (!options.InlineMethods || !options.RemoveInlinedMethods)
				return;
			FindAndRemoveInlinedMethods();
		}

		/// <summary>
		/// Returns metadata tokens for all string decrypter methods (both standard and generic).
		/// Used by the DynamicStringInliner to register methods with the AssemblyData subprocess,
		/// and by the pipeline to know which methods should be treated as string decrypters.
		/// </summary>
		public override IEnumerable<int> GetStringDecrypterMethods() {
			var list = stringDecrypter.DecrypterInfos.Select(info => info.method.MDToken.ToInt32()).ToList();
			if (stringDecrypter.OtherStringDecrypter != null)
				list.Add(stringDecrypter.OtherStringDecrypter.MDToken.ToInt32());
			list.AddRange(genericConstantDecrypter.DecrypterMethods.Select(dm => dm.method.MDToken.ToInt32()));
			return list;
		}

		public override void OnWriterEvent(ModuleWriterBase writer, ModuleWriterEvent evt) {
			if (!options.DecryptMethods || !methodsDecrypter.HasNativeMethods)
				return;
			switch (evt) {
			case ModuleWriterEvent.Begin:
				// The decrypter assumes RVAs are unique so don't share any method bodies
				writer.TheOptions.ShareMethodBodies = false;
				break;

			case ModuleWriterEvent.MDBeginAddResources:
				methodsDecrypter.PrepareEncryptNativeMethods(writer);
				break;

			case ModuleWriterEvent.BeginWriteChunks:
				methodsDecrypter.EncryptNativeMethods(writer);
				break;
			}
		}

		protected override void Dispose(bool disposing) {
			if (disposing)
				FreePEImage();
			base.Dispose(disposing);
		}
	}
}
