// The predicate that must NOT be folded, and the reason the pass reads the callee instead of
// guessing from its last instruction.
//
// Pred::Test is `return Pick() > 0` -- an ordinary method, in the exact position an opaque predicate
// occupies. Its body ends `cgt; ret`, so a heuristic that folds anything not ending in `ldc.i4.0`
// calls it a constant true and deletes the Dead arm, which is live code.
.assembly extern mscorlib { .ver 4:0:0:0 }
.assembly opaque_real_computation { }
.module opaque_real_computation.dll

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
  .method public hidebysig static bool Test() cil managed
  {
    .maxstack 2
      call int32 Src::Pick()
      ldc.i4.0
      cgt
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
