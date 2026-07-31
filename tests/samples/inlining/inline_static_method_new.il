.assembly extern mscorlib
{
  .publickeytoken = (B7 7A 5C 56 19 34 E0 89 )                         // .z\V.4..
  .ver 4:0:0:0
}
.assembly A
{
}

.class public auto ansi abstract sealed beforefieldinit C
    extends System.Object
{
    .method static privatescope 
        valuetype [mscorlib]System.DateTime '\u200b' (
            int32 p0,
            int32 p1,
            int32 p2,
            int32 p3,
            int32 p4,
            int32 p5
        ) cil managed 
    {
        // Header Size: 12 bytes
        // Code Size: 8 (0x8) bytes
        // LocalVarSig Token: 0x11000006 RID: 6
        .maxstack 6
        .locals init (
            [0] uint32
        )
		
        IL_0000: ldarg.0
        IL_0001: ldarg.1
        IL_0002: ldarg.2
        IL_0003: ldarg.3
        IL_0004: ldarg.s p4
        IL_0005: ldarg.s p5
        IL_0006: newobj    instance void [mscorlib]System.DateTime::.ctor(int32, int32, int32, int32, int32, int32)
        IL_000B: ret
    }
    .method static public valuetype [mscorlib]System.DateTime test() cil managed 
    {
        .maxstack 6

		ldc.i4 1970
		ldc.i4.1
		ldc.i4.1
		ldc.i4.0
		ldc.i4.0
		ldc.i4.0
        call      valuetype [mscorlib]System.DateTime C::'\u200b'(int32, int32, int32, int32, int32, int32)
        ret
    }
}
