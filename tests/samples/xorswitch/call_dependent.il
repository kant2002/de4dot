// Generated fixture -- see tests/run_xorswitch_tests.py for what it asserts and why.
.assembly extern mscorlib { .ver 4:0:0:0 }
.assembly call_dependent { }
.module call_dependent.dll

.class public auto ansi abstract sealed beforefieldinit Marker
    extends [mscorlib]System.Object
{
  .method public hidebysig static void A() cil managed { .maxstack 1  ret }
  .method public hidebysig static void B() cil managed { .maxstack 1  ret }
  .method public hidebysig static int32 Opaque() cil managed { .maxstack 1  ldc.i4.7  ret }
}

.class public auto ansi abstract sealed beforefieldinit Target
    extends [mscorlib]System.Object
{
  .method public hidebysig static void Run() cil managed
  {
    .maxstack 4
    .locals init (uint32 V_0)
      call int32 Marker::Opaque()
      br OUTER

    XFORM:                       // outer case 0 -- transform reads the carried state
      ldloc.0
      ldc.i4 1524534657
      mul
      ldc.i4 0x2EEB6183
      xor
      br INNER

    RETB:                        // outer case 1 -- the exit
      ret

    SEED:                        // outer case 2 -- constant re-seed
      ldc.i4 0x11110000

    INNER:                       // second dispatch site; state arrives on the stack
      ldc.i4 0x5A5A5A5A
      xor
      dup
      stloc.0
      ldc.i4.3
      rem.un
      switch (PA, PB, SEED)
      br RETB

    PA:
      call void Marker::A()
      ldc.i4.0
      br OUTER

    PB:
      call void Marker::B()
      ldc.i4.1
      br OUTER

    OUTER:                       // first dispatch site
      switch (XFORM, RETB, SEED)
      br RETB
  }
}
