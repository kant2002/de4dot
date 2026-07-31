
//  Microsoft (R) .NET Framework IL Disassembler.  Version 4.8.4084.0
//  Copyright (c) Microsoft Corporation.  All rights reserved.



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
.module inline_static_method.dll
// MVID: {1DB060EA-18B3-4578-A7CD-7F0B8D95FCAE}
.imagebase 0x00400000
.file alignment 0x00000200
.stackreserve 0x00100000
.subsystem 0x0003       // WINDOWS_CUI
.corflags 0x00000001    //  ILONLY
// Image base: 0x000001387D460000


// =============== CLASS MEMBERS DECLARATION ===================

.class public abstract auto ansi sealed beforefieldinit C
       extends [mscorlib]System.Object
{
  .method public static string  test() cil managed
  {
    // Code size       12 (0xc)
    .maxstack  2
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

} // end of class C

.class private abstract auto ansi dummy_ptr.'{45d46a36-993b-4434-8694-f95e840ec4d2}'
       extends [mscorlib]System.Object
{
  .method public hidebysig newslot abstract virtual 
          instance void  m000001() cil managed
  {
  } // end of method '{45d46a36-993b-4434-8694-f95e840ec4d2}'::m000001

} // end of class dummy_ptr.'{45d46a36-993b-4434-8694-f95e840ec4d2}'


// =============================================================

// *********** DISASSEMBLY COMPLETE ***********************
