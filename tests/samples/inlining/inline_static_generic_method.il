.assembly extern mscorlib
{
  .publickeytoken = (B7 7A 5C 56 19 34 E0 89 )                         // .z\V.4..
  .ver 4:0:0:0
}
.assembly A
{
}

.class public auto ansi abstract sealed beforefieldinit C`1<T>
    extends System.Object
{
    .method static privatescope 
        string '\u200b' (
            string '',
            string ''
        ) cil managed 
    {
        // Header Size: 12 bytes
        // Code Size: 8 (0x8) bytes
        // LocalVarSig Token: 0x11000006 RID: 6
        .maxstack 4
        .locals init (
            [0] uint32
        )

        IL_0000: ldarg.0
        IL_0001: ldarg.1
        IL_0002: call      string [mscorlib]System.String::Concat(string, string)
        IL_0007: ret
    }
    .method static public string test() cil managed 
    {
        .locals init (
            [0] string v1,
            [1] string v2
        )
        ldnull
        stloc.0
        ldnull
        stloc.1
        ldloc.0
        ldloc.1
        call      string class C`1<!T>::'\u200b'(string, string)
        ret
    }
}
