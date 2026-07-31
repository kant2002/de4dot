
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
.module inline_static_method_new.dll
// MVID: {B7BBB84E-5BB4-41B0-9F73-6AA28456E8BC}
.imagebase 0x00400000
.file alignment 0x00000200
.stackreserve 0x00100000
.subsystem 0x0003       // WINDOWS_CUI
.corflags 0x00000001    //  ILONLY
// Image base: 0x0000016BCEA30000


// =============== CLASS MEMBERS DECLARATION ===================

.class public abstract auto ansi sealed beforefieldinit C
       extends [mscorlib]System.Object
{
  .method public static valuetype [mscorlib]System.DateTime 
          test() cil managed
  {
    // Code size       16 (0x10)
    .maxstack  8
    IL_0000:  ldc.i4     0x7b2
    IL_0005:  ldc.i4.1
    IL_0006:  ldc.i4.1
    IL_0007:  ldc.i4.0
    IL_0008:  ldc.i4.0
    IL_0009:  ldc.i4.0
    IL_000a:  call       instance void [mscorlib]System.DateTime::.ctor(int32,
                                                                        int32,
                                                                        int32,
                                                                        int32,
                                                                        int32,
                                                                        int32)
    IL_000f:  ret
  } // end of method C::test

} // end of class C

.class private abstract auto ansi dummy_ptr.'{54d267df-e19e-488d-9ec6-4492ec3a829c}'
       extends [mscorlib]System.Object
{
  .method public hidebysig newslot abstract virtual 
          instance void  m000001() cil managed
  {
  } // end of method '{54d267df-e19e-488d-9ec6-4492ec3a829c}'::m000001

} // end of class dummy_ptr.'{54d267df-e19e-488d-9ec6-4492ec3a829c}'


// =============================================================

// *********** DISASSEMBLY COMPLETE ***********************
