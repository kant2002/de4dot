// Identical to opaque_fold in every respect except one: something assigns Pred::P, so the predicate
// is real code and both arms are live.
//
// The assignment is made through `ldsflda` rather than `stsfld`, which is the whole point of the
// fixture. A write check that looks for stores sees nothing here, concludes the field is still null,
// and folds a predicate whose answer it does not know. NeverWrittenStaticFields counts any reference
// that is not a plain load as a possible assignment for exactly this reason.
.assembly extern mscorlib { .ver 4:0:0:0 }
.assembly opaque_field_written_by_address { }
.module opaque_field_written_by_address.dll

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

  .method public hidebysig static void Assign() cil managed
  {
    .maxstack 2
      ldsflda object Pred::P
      newobj instance void [mscorlib]System.Object::.ctor()
      stind.ref
      ret
  }
}

.class public auto ansi abstract sealed beforefieldinit Target
    extends [mscorlib]System.Object
{
  .method public hidebysig static void Run() cil managed
  {
    .maxstack 4
      call void Pred::Assign()
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
