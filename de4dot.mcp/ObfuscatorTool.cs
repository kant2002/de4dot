using System.ComponentModel;
using System.Reflection;
using de4dot.code;
using de4dot.code.AssemblyClient;
using de4dot.code.deobfuscators;
using dnlib.DotNet;
using ModelContextProtocol.Server;

namespace de4dot.mcp {

	/// <summary>
	/// This tool help deobfuscate
	/// </summary>
	[McpServerToolType]
	public sealed class ObfuscatorTool {
		[McpServerTool(Name = "detectObfuscator"), Description("Detect obfuscator from specified DLL file")]
		public static string DetectObfuscator(
			McpServer thisServer,
			[Description("The file to detect obfuscator obfuscate")] string obfuscatedFile,
			CancellationToken cancellationToken) {
			return DetectDebugger(obfuscatedFile);
		}

		static string DetectDebugger(string fileName) {
			var moduleContext = new ModuleContext(TheAssemblyResolver.Instance);
			var assemblyClientFactory = new NewAppDomainAssemblyClientFactory();
			var options = new ObfuscatedFile.Options() {
				ControlFlowDeobfuscation = false,
				KeepObfuscatorTypes = true,
				Filename = fileName,
				StringDecrypterType = DecrypterType.None,
			};
			var file = new ObfuscatedFile(options, moduleContext, assemblyClientFactory);
			var deobfuscatorContext = new DeobfuscatorContext();
			file.DeobfuscatorContext = deobfuscatorContext;
			file.Load(CreateDeobfuscators());
			var deob = file.Deobfuscator;
			TheAssemblyResolver.Instance.Remove(file.ModuleDefMD);
			return deob.TypeLong;
		}
		[McpServerTool(Name = "deobfuscate"), Description("Deobfuscate from specified DLL file")]
		public static string Deobfuscate(
			McpServer thisServer,
			[Description("The file to deobfuscate")] string obfuscatedFile,
			[Description("Method to deobfuscate")] string method,
			CancellationToken cancellationToken) {
			var moduleContext = new ModuleContext(TheAssemblyResolver.Instance);
			var assemblyClientFactory = new NewAppDomainAssemblyClientFactory();
			var options = new ObfuscatedFile.Options() {
				ControlFlowDeobfuscation = false,
				KeepObfuscatorTypes = true,
				Filename = obfuscatedFile,
				StringDecrypterType = DecrypterType.None,
				NewFilename = Path.ChangeExtension(obfuscatedFile, ".deobfuscate.dll"),
			};
			var file = new ObfuscatedFile(options, moduleContext, assemblyClientFactory);
			var deobfuscatorContext = new DeobfuscatorContext();
			file.DeobfuscatorContext = deobfuscatorContext;
			file.Load(CreateDeobfuscators());
			var deob = file.Deobfuscator;
			file.DeobfuscateBegin();
			file.Deobfuscate();
			file.DeobfuscateEnd();
			file.Save();

			TheAssemblyResolver.Instance.Remove(file.ModuleDefMD);
			return options.NewFilename;
		}

		static IList<IDeobfuscator> CreateDeobfuscators() {
			return [.. CreateDeobfuscatorInfos().Select(_ => _.CreateDeobfuscator())];
		}

		static IList<IDeobfuscatorInfo> CreateDeobfuscatorInfos() {
			var local = new List<IDeobfuscatorInfo> {
				new de4dot.code.deobfuscators.Unknown.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.Agile_NET.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.Babel_NET.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.CodeFort.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.CodeVeil.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.CodeWall.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.Confuser.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.CryptoObfuscator.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.DeepSea.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.Dotfuscator.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.dotNET_Reactor.v3.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.dotNET_Reactor.v4.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.Eazfuscator_NET.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.Goliath_NET.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.ILProtector.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.MaxtoCode.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.MPRESS.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.Obfuscar.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.Rummage.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.Skater_NET.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.SmartAssembly.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.Spices_Net.DeobfuscatorInfo(),
				new de4dot.code.deobfuscators.Xenocode.DeobfuscatorInfo(),
			};
			return local;
		}

		static IList<IDeobfuscatorInfo> LoadPlugin(string assembly) {
			var plugins = new List<IDeobfuscatorInfo>();
			try {
				foreach (var item in Assembly.LoadFile(assembly).GetTypes()) {
					var interfaces = new List<Type>(item.GetInterfaces());
					if (item.IsClass && interfaces.Contains(typeof(IDeobfuscatorInfo)))
						plugins.Add((IDeobfuscatorInfo)Activator.CreateInstance(item));
				}
			}
			catch {
			}
			return plugins;
		}
	}
}
