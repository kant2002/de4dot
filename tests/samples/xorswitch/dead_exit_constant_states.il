// The shape that hid behind "undecidable": the exit IS a switch target, so it is reachable in the
// control-flow graph and the method verifies, but no state value the machine can produce selects it.
// States cycle 0 -> 1 -> 0 forever and case 2 is dead. The tracer must prove Loops, and selection
// must therefore reject the resolved form.
.assembly extern mscorlib { .ver 4:0:0:0 }
.assembly dead_exit_constant_states { }
.module dead_exit_constant_states.dll

.class public auto ansi abstract sealed beforefieldinit Marker
    extends [mscorlib]System.Object
{
  .method public hidebysig static void A() cil managed { .maxstack 1  ret }
  .method public hidebysig static void B() cil managed { .maxstack 1  ret }
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
      ldloc.0
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
