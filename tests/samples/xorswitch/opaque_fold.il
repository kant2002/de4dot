// The call-based opaque predicate DotNetReactorCflowDeobfuscator exists to fold.
//
// Pred::P is a private static object nothing in the module assigns, so it is still null, so
// `P == null` is a constant true and the Dead arm is unreachable. The switch is there only because
// the pass declines to look at methods without one, and its value comes out of a call so nothing
// else collapses it and steals the result being asserted here.
.assembly extern mscorlib { .ver 4:0:0:0 }
.assembly opaque_fold { }
.module opaque_fold.dll

.class public auto ansi abstract sealed beforefieldinit Marker
    extends [mscorlib]System.Object
{
  .method public hidebysig static void Live() cil managed { .maxstack 1  ret }
  .method public hidebysig static void Dead() cil managed { .maxstack 1  ret }
}

.class public auto ansi abstract sealed beforefieldinit Src
    extends [mscorlib]System.Object
{
  .method public hidebysig static int32 Pick() cil managed { .maxstack 1  ldc.i4.0  ret }
}

.class public auto ansi abstract sealed beforefieldinit Pred
    extends [mscorlib]System.Object
{
  .field private static object P

  .method public hidebysig static bool Test() cil managed
  {
    .maxstack 2
      ldsfld object Pred::P
      ldnull
      ceq
      ret
  }
}

.class public auto ansi abstract sealed beforefieldinit Target
    extends [mscorlib]System.Object
{
  .method public hidebysig static void Run() cil managed
  {
    .maxstack 4
      call int32 Src::Pick()
      switch (S0, S1)
      br AFTER

    S0:
      br AFTER

    S1:
      br AFTER

    AFTER:
      ldnull
      call bool Pred::Test()
      brtrue LIVE

      pop
      call void Marker::Dead()
      br END

    LIVE:
      pop
      call void Marker::Live()

    END:
      ret
  }
}
