using de4dot.code.deobfuscators;
using dnlib.DotNet;

namespace de4dot.code.tests {
	/// <summary>
	/// <see cref="UnusedMethodsFinder"/> is handed a set of methods that look unused and answers with
	/// the ones that really are: it walks every method body in the module and maps each call operand
	/// back to the definition it refers to, striking off whatever it finds a caller for. Whatever it
	/// returns gets deleted, so a callee the walk fails to recognise is a live method removed and a
	/// call site left pointing at a reference with no definition behind it.
	///
	/// A call to a method on a *generic instantiation* of a type in the module — <c>callvirt instance
	/// !0 Box`1&lt;int32&gt;::Get()</c> — is one such operand: a MemberRef whose class is a TypeSpec,
	/// whose signature is written in terms of the type's own generic parameters and so matches nothing
	/// when compared against the TypeDef's methods.
	///
	/// The module under test is this test assembly itself, so the operands walked are whatever the C#
	/// compiler really emits rather than hand-built metadata that might not agree with it.
	/// </summary>
	[TestClass]
	public sealed class UnusedMethodsFinderTest {
		/// <summary>A generic type, so calls against an instantiation of it go through a TypeSpec.</summary>
		sealed class Box<T> {
			public T? Value;
			public T? Get() => Value;
			public static Box<T> Create() => new Box<T>();
			public T? NeverCalled() => Value;
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
			var module = ModuleDefMD.Load(typeof(UnusedMethodsFinderTest).Module);
			// The walk asks dnlib to resolve call operands, which needs a resolver. de4dot's own
			// loader always supplies one; a bare Load does not.
			module.Context = ModuleDef.CreateModuleContext();
			return module;
		}

		static MethodDef FindMethod(ModuleDefMD module, string declaringType, string name) {
			foreach (var type in module.GetTypes()) {
				if (type.Name != declaringType) {
					continue;
				}
				foreach (var method in type.Methods) {
					if (method.Name == name) {
						return method;
					}
				}
			}
			throw new InvalidOperationException($"{declaringType}::{name} was not found in the loaded module");
		}

		/// <summary>
		/// Asks whether <paramref name="candidate"/> would be deleted, with nothing else offered as a
		/// deletion candidate — so the answer turns purely on whether a caller was recognised.
		/// </summary>
		static bool IsReportedUnused(ModuleDefMD module, MethodDef candidate) {
			var finder = new UnusedMethodsFinder(module, new[] { candidate }, new MethodCollection());
			foreach (var unused in finder.Find()) {
				if (unused == candidate) {
					return true;
				}
			}
			return false;
		}

		static void AssertNotDeleted(ModuleDefMD module, string declaringType, string name) {
			var method = FindMethod(module, declaringType, name);
			Assert.IsFalse(IsReportedUnused(module, method),
				$"{declaringType}::{name} is called from this assembly, but was reported unused — " +
				"it would be deleted and its call site left dangling");
		}

		/// <summary>The case this exists for: the callee is reached only through a TypeSpec.</summary>
		[TestMethod]
		public void AMethodCalledOnAGenericInstantiationIsNotUnused() =>
			AssertNotDeleted(LoadThisModule(), "Box`1", nameof(Box<int>.Get));

		[TestMethod]
		public void AStaticMethodCalledOnAGenericInstantiationIsNotUnused() =>
			AssertNotDeleted(LoadThisModule(), "Box`1", nameof(Box<int>.Create));

		/// <summary>The ordinary case still works — recognising TypeSpecs must not shadow it.</summary>
		[TestMethod]
		public void AMethodCalledOnANonGenericTypeIsNotUnused() =>
			AssertNotDeleted(LoadThisModule(), "Plain", nameof(Plain.Get));

		/// <summary>
		/// The other direction: a method on a generic type that nothing calls is still found. Without
		/// this, resolving every operand to *something* would pass the tests above and quietly stop the
		/// finder from ever removing anything.
		/// </summary>
		[TestMethod]
		public void AMethodNothingCallsIsStillUnused() {
			var module = LoadThisModule();
			var method = FindMethod(module, "Box`1", nameof(Box<int>.NeverCalled));
			Assert.IsTrue(IsReportedUnused(module, method));
		}
	}
}
