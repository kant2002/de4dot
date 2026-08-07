// The same fold as opaque_fold, but on an ASSEMBLY-visible field rather than a private one.
//
// Reactor's predicate field is internal, not private, and the visibility rule in
// NeverWrittenStaticFields is the part of the analysis most likely to refuse it by accident: an
// internal field is out of reach of other assemblies only while this assembly stays single-module
// and declares no InternalsVisibleTo. Both hold here, so the fold must happen -- and if the rule is
// ever tightened to private-only, this is the fixture that says so instead of a corpus regression
// nobody can attribute.
.assembly extern mscorlib { .ver 4:0:0:0 }
.assembly opaque_field_compare_null { }
.module opaque_field_compare_null.dll

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
  .field assembly static object P

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
