
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
.module inline_static_method_br_target.dll
// MVID: {1F592123-31D6-44A8-B49A-59C0BE326443}
.imagebase 0x00400000
.file alignment 0x00000200
.stackreserve 0x00100000
.subsystem 0x0003       // WINDOWS_CUI
.corflags 0x00000001    //  ILONLY
// Image base: 0x00000256030E0000


// =============== CLASS MEMBERS DECLARATION ===================

.class public abstract auto ansi sealed beforefieldinit C
       extends [mscorlib]System.Object
{
  .method public static string  test() cil managed
  {
    // Code size       18 (0x12)
    .maxstack  3
    .locals init (string V_0,
             string V_1)
    IL_0000:  ldnull
    IL_0001:  stloc.0
    IL_0002:  ldnull
    IL_0003:  stloc.1
    IL_0004:  ldloc.0
    IL_0005:  ldloc.1
    IL_0006:  ldc.i4.0
    IL_0007:  brtrue     IL_0000

    IL_000c:  call       string [mscorlib]System.String::Concat(string,
                                                                string)
    IL_0011:  ret
  } // end of method C::test

} // end of class C

.class private abstract auto ansi dummy_ptr.'{cf5b5b4f-4214-4303-9fee-930616361fa9}'
       extends [mscorlib]System.Object
{
  .method public hidebysig newslot abstract virtual 
          instance void  m000001() cil managed
  {
  } // end of method '{cf5b5b4f-4214-4303-9fee-930616361fa9}'::m000001

} // end of class dummy_ptr.'{cf5b5b4f-4214-4303-9fee-930616361fa9}'


// =============================================================

// *********** DISASSEMBLY COMPLETE ***********************
