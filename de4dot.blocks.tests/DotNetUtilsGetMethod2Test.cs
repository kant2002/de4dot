using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.blocks.tests {
	/// <summary>
	/// <see cref="DotNetUtils.GetMethod2(ModuleDef, IMethod)"/> maps a call operand back to the
	/// <see cref="MethodDef"/> it refers to, when that definition lives in the module being analysed.
	///
	/// What makes this worth pinning is the direction of its failure. <c>UnusedMethodsFinder</c> asks
	/// this question of every call operand in the module and treats a null as "nothing calls the
	/// callee", so a lookup that wrongly answers null does not merely miss an optimisation — it
	/// deletes a method that is still called and leaves a dangling reference behind. The tests below
	/// are therefore mostly about *not* answering null.
	///
	/// The module under test is this test assembly itself, so the operands are whatever the C#
	/// compiler really emits rather than hand-built metadata that might not match it.
	/// </summary>
	[TestClass]
	public sealed class DotNetUtilsGetMethod2Test {
		/// <summary>A generic type, so calls against an instantiation of it go through a TypeSpec.</summary>
		sealed class Box<T> {
			public T? Value;
			public T? Get() => Value;
			public static Box<T> Create() => new Box<T>();
		}

		sealed class Plain {
			public int Get() => 1;
		}

		// The call sites. Each is compiled by the real compiler and then read back out of the
		// assembly; none of them is executed.
		static int CallsAMethodOnAGenericInstantiation() {
			var box = new Box<int> { Value = 7 };
			return box.Get();
		}

		static Box<string> CallsAStaticMethodOnAGenericInstantiation() => Box<string>.Create();

		static int CallsAMethodOnANonGenericType() => new Plain().Get();

		static ModuleDefMD LoadThisModule() {
			var module = ModuleDefMD.Load(typeof(DotNetUtilsGetMethod2Test).Module);
			// GetMethod2 asks dnlib to resolve the reference, which needs a resolver. de4dot's own
			// loader always supplies one; a bare Load does not.
			module.Context = ModuleDef.CreateModuleContext();
			return module;
		}

		static MethodDef FindMethod(ModuleDefMD module, string name) {
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (method.Name == name)
						return method;
				}
			}
			throw new InvalidOperationException($"{name} was not found in the loaded module");
		}

		/// <summary>Resolves the single call operand in <paramref name="callSite"/>.</summary>
		static MethodDef? ResolveTheCallIn(ModuleDefMD module, string callSite, string calleeName) {
			var body = FindMethod(module, callSite).Body;
			foreach (var instr in body.Instructions) {
				if (instr.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj &&
						instr.Operand is IMethod called && called.Name == calleeName) {
					return DotNetUtils.GetMethod2(module, called);
				}
			}
			throw new InvalidOperationException($"no call to {calleeName} was found in {callSite}");
		}

		static void AssertResolvesTo(MethodDef? actual, string expectedName, string expectedDeclaringType) {
			Assert.IsNotNull(actual,
				$"expected {expectedDeclaringType}::{expectedName}; a null here reads as " +
				"'this method is never called' and gets it deleted");
			Assert.AreEqual(expectedName, actual.Name.String);
			Assert.AreEqual(expectedDeclaringType, actual.DeclaringType.Name.String);
		}

		/// <summary>
		/// The case this exists for: <c>callvirt instance !0 Box`1&lt;int32&gt;::Get()</c> is a MemberRef
		/// whose class is a TypeSpec. Matching the operand's signature against the TypeDef's methods
		/// fails, because the operand's signature is written in terms of the type's own generic
		/// parameters; resolving the reference is what gets the right answer.
		/// </summary>
		[TestMethod]
		public void AnInstanceCallOnAGenericInstantiationResolves() {
			var module = LoadThisModule();
			AssertResolvesTo(ResolveTheCallIn(module, nameof(CallsAMethodOnAGenericInstantiation), "Get"),
				"Get", "Box`1");
		}

		[TestMethod]
		public void AStaticCallOnAGenericInstantiationResolves() {
			var module = LoadThisModule();
			AssertResolvesTo(ResolveTheCallIn(module, nameof(CallsAStaticMethodOnAGenericInstantiation), "Create"),
				"Create", "Box`1");
		}

		/// <summary>The ordinary case still works — the new path must not shadow it.</summary>
		[TestMethod]
		public void ACallOnANonGenericTypeStillResolves() {
			var module = LoadThisModule();
			AssertResolvesTo(ResolveTheCallIn(module, nameof(CallsAMethodOnANonGenericType), "Get"),
				"Get", "Plain");
		}

		/// <summary>
		/// A call into another assembly resolves to a definition in a different module, and must not
		/// be reported as a method of the module under analysis: callers would then delete, rename or
		/// rewrite something they do not own.
		/// </summary>
		[TestMethod]
		public void ACallIntoAnotherAssemblyIsNotClaimedAsOurs() {
			var module = LoadThisModule();
			var corlibCall = new MemberRefUser(module, "ToString",
				MethodSig.CreateInstance(module.CorLibTypes.String),
				module.CorLibTypes.Object.TypeDefOrRef);
			Assert.IsNull(DotNetUtils.GetMethod2(module, corlibCall));
		}

		[TestMethod]
		public void ANullOperandIsNull() {
			var module = LoadThisModule();
			Assert.IsNull(DotNetUtils.GetMethod2(module, null));
		}

		/// <summary>A MethodDef is already the answer and is returned as-is, whatever module it is in.</summary>
		[TestMethod]
		public void AMethodDefIsReturnedUnchanged() {
			var module = LoadThisModule();
			var method = FindMethod(module, nameof(CallsAMethodOnANonGenericType));
			Assert.AreSame(method, DotNetUtils.GetMethod2(module, method));
		}
	}
}
