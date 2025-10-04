using de4dot.code;
using dnlib.DotNet;

namespace de4dot.mcp {
	public class DeobfuscationSession {
		public AssemblyResolver AssemblyResolver { get; } = new TheAssemblyResolver();
		public DeobfuscatorContext DeobfuscatorContext { get; } = new();
	}
}
