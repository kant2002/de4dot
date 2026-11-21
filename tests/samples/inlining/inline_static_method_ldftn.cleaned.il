
//  Microsoft (R) .NET Framework IL Disassembler.  Version 4.8.3928.0
//  Copyright (c) Microsoft Corporation. All rights reserved.



// Metadata version: v4.0.30319
.assembly extern mscorlib
{
  .publickeytoken = (B7 7A 5C 56 19 34 E0 89 )                         // .z\V.4..
  .ver 4:0:0:0
}
.assembly A
{
  .ver 0:0:0:0
}
.module inline_static_method_ldftn.dll
// MVID: {D44651D8-ED5A-4094-A8DC-40FF9F7844A1}
.imagebase 0x00400000
.file alignment 0x00000200
.stackreserve 0x00100000
.subsystem 0x0003       // WINDOWS_CUI
.corflags 0x00000001    //  ILONLY
// Image base: 0x000001F0B8CF0000


// =============== CLASS MEMBERS DECLARATION ===================

.class public abstract auto ansi sealed beforefieldinit C
       extends [mscorlib]System.Object
{
  .method privatescope static string  u200b$PST06000001(string A_0,
                                                        string A_1) cil managed
  {
    // Code size       8 (0x8)
    .maxstack  2
    .locals init (uint32 V_0)
    IL_0000:  ldarg.0
    IL_0001:  ldarg.1
    IL_0002:  call       string [mscorlib]System.String::Concat(string,
                                                                string)
    IL_0007:  ret
  } // end of method C::u200b

  .method public static string  test() cil managed
  {
    // Code size       12 (0xc)
    .maxstack  8
    .locals init (string V_0,
             string V_1)
    IL_0000:  ldnull
    IL_0001:  stloc.0
    IL_0002:  ldnull
    IL_0003:  stloc.1
    IL_0004:  ldloc.0
    IL_0005:  ldloc.1
    IL_0006:  call       string [mscorlib]System.String::Concat(string,
                                                                string)
    IL_000b:  ret
  } // end of method C::test

  .method public static class [mscorlib]System.Func`3<string,string,string> 
          test_delegate() cil managed
  {
    // Code size       13 (0xd)
    .maxstack  8
    IL_0000:  ldnull
    IL_0001:  ldftn      string C::u200b$PST06000001(string,
                                                     string)
    IL_0007:  newobj     instance void class [mscorlib]System.Func`3<string,string,string>::.ctor(object,
                                                                                                  native int)
    IL_000c:  ret
  } // end of method C::test_delegate

} // end of class C


// =============================================================

// *********** DISASSEMBLY COMPLETE ***********************
