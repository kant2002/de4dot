// Same shape, one difference that must change the answer: the dispatch operand comes out of a call,
// so it cannot be bounded. Every target stays possible, which makes the exit reachable, so this must
// NOT be judged Loops and must NOT be rejected. It is the guard against generalising the constant
// case unsafely -- an implementation that assumed the visible ldc.i4 values were the whole story
// would reject this and delete a correct resolution.
.assembly extern mscorlib { .ver 4:0:0:0 }
.assembly dead_exit_unknown_feeder { }
.module dead_exit_unknown_feeder.dll

.class public auto ansi abstract sealed beforefieldinit Marker
    extends [mscorlib]System.Object
{
  .method public hidebysig static void A() cil managed { .maxstack 1  ret }
  .method public hidebysig static void B() cil managed { .maxstack 1  ret }
  .method public hidebysig static int32 Pick() cil managed { .maxstack 1  ldc.i4.2  ret }
}

.class public auto ansi abstract sealed beforefieldinit Target
    extends [mscorlib]System.Object
{
  .method public hidebysig static void Run() cil managed
  {
    .maxstack 2
    .locals init (int32 V_0)
      ldc.i4.0
      stloc.0

    DISPATCH:
      call int32 Marker::Pick()
      switch (CASE0, CASE1, EXIT)
      br DISPATCH

    CASE0:
      call void Marker::A()
      ldc.i4.1
      stloc.0
      br DISPATCH

    CASE1:
      call void Marker::B()
      ldc.i4.0
      stloc.0
      br DISPATCH

    EXIT:
      ret
  }
}
