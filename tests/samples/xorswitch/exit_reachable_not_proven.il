// Why the verdict is called ExitReachable and not Terminates.
//
// An untracked condition sends control either to a `ret` or into a machine that provably cycles
// forever. Exploration widens at the branch and takes both, so an exit turns up in the reachable set
// -- but half of the real executions never terminate. The correct verdict is therefore "no proof of
// non-termination", NOT "this terminates", and nothing may be rejected on the strength of it.
.assembly extern mscorlib { .ver 4:0:0:0 }
.assembly exit_reachable_not_proven { }
.module exit_reachable_not_proven.dll

.class public auto ansi abstract sealed beforefieldinit Marker
    extends [mscorlib]System.Object
{
  .method public hidebysig static void A() cil managed { .maxstack 1  ret }
  .method public hidebysig static void B() cil managed { .maxstack 1  ret }
  .method public hidebysig static bool Choose() cil managed { .maxstack 1  ldc.i4.1  ret }
}

.class public auto ansi abstract sealed beforefieldinit Target
    extends [mscorlib]System.Object
{
  .method public hidebysig static void Run() cil managed
  {
    .maxstack 2
    .locals init (int32 V_0)
      call bool Marker::Choose()
      brtrue RETURNS

      ldc.i4.0
      stloc.0

    DISPATCH:
      ldloc.0
      switch (CASE0, CASE1)
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

    RETURNS:
      ret
  }
}
