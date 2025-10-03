Basic Block transformation
==========================

This is technique that breaks down the control flow of a method into smaller basic blocks and then rearranges these blocks in a non-linear order. This makes it difficult for an attacker to understand the original logic of the code.

Process if following. It's simplified and don't account for protected blocks (try/catch/finally):
1. We extract basic blocks from method body. A basic block is a sequence of instructions that has a single entry point and a single exit point. 
1. We generate unique numbers for each basic block. Let's say in 1000 range.
1. We resort basic blocks based on unique orders.
1. Create a dispatcher block that will use a switch statement to jump to the correct basic block based on a control variable.

For example, consider the following simple method:
```cil
// RaisePropertyChanged(propertyExpression);
IL_0000: ldarg.0
IL_0001: ldarg.1
IL_0002: callvirt instance void GalaSoft.MvvmLight.ObservableObject::RaisePropertyChanged<!!T>(class [System.Linq.Expressions]System.Linq.Expressions.Expression`1<class [System.Runtime]System.Func`1<!!0>>)
// if (broadcast)
IL_0007: ldarg.s broadcast
IL_0009: brfalse.s IL_001b

// string propertyName = ObservableObject.GetPropertyName(propertyExpression);
IL_000b: ldarg.1
IL_000c: call string GalaSoft.MvvmLight.ObservableObject::GetPropertyName<!!T>(class [System.Linq.Expressions]System.Linq.Expressions.Expression`1<class [System.Runtime]System.Func`1<!!0>>)
IL_0011: stloc.0
// Broadcast(oldValue, newValue, propertyName);
IL_0012: ldarg.0
IL_0013: ldarg.2
IL_0014: ldarg.3
IL_0015: ldloc.0
IL_0016: callvirt instance void GalaSoft.MvvmLight.ViewModelBase::Broadcast<!!T>(!!0, !!0, string)

// }
IL_001b: ret
```

We have 3 basic blocks here:

First: 
```cil
// RaisePropertyChanged(propertyExpression);
IL_0000: ldarg.0
IL_0001: ldarg.1
IL_0002: callvirt instance void GalaSoft.MvvmLight.ObservableObject::RaisePropertyChanged<!!T>(class [System.Linq.Expressions]System.Linq.Expressions.Expression`1<class [System.Runtime]System.Func`1<!!0>>)
// if (broadcast)
IL_0007: ldarg.s broadcast
IL_0009: brfalse.s IL_001b
```

Second:
```cil
// string propertyName = ObservableObject.GetPropertyName(propertyExpression);
IL_000b: ldarg.1
IL_000c: call string GalaSoft.MvvmLight.ObservableObject::GetPropertyName<!!T>(class [System.Linq.Expressions]System.Linq.Expressions.Expression`1<class [System.Runtime]System.Func`1<!!0>>)
IL_0011: stloc.0
// Broadcast(oldValue, newValue, propertyName);
IL_0012: ldarg.0
IL_0013: ldarg.2
IL_0014: ldarg.3
IL_0015: ldloc.0
IL_0016: callvirt instance void GalaSoft.MvvmLight.ViewModelBase::Broadcast<!!T>(!!0, !!0, string)
```

Third:
```cil
// }
IL_001b: ret
```

Let's say our unique numbers for these blocks are: 132, 456, 789.

Now we will create 1 local variable for storing the control variable:
```cil
.locals init (
	[0] string propertyName,
	[1] int32 controlVar
)
```

Initialize `controlVar`:
```cil
         ldc.i4.132
		 stloc.1
IL_001c: br.s IL_0020
```

Then we create a dispatcher block:
```cil
IL_0020: ldloc.1
IL_0021: switch (IL_0030, IL_0040, IL_0050)
         br.s IL_0060

		 // Block 132
IL_0030: ldarg.0
IL_0031: ldarg.1
IL_0032: callvirt instance void GalaSoft.MvvmLight.ObservableObject::RaisePropertyChanged<!!T>(class [System.Linq.Expressions]System.Linq.Expressions.Expression`1<class [System.Runtime]System.Func`1<!!0>>)
IL_0037: ldarg.s broadcast
		 ldc.i4.789
         stloc.1
IL_0039: brfalse.s IL_0020
		 ldc.i4.456
		 stloc.1
		 br.s IL_0020

		 // Block 456
IL_0040: ldarg.1
IL_0041: call string GalaSoft.MvvmLight.ObservableObject::GetPropertyName<!!T>(class [System.Linq.Expressions]System.LinqExpressions.Expression`1<class [System.Runtime]System.Func`1<!!0>>)
IL_0046: stloc.0
IL_0047: ldarg.0
IL_0048: ldarg.2
IL_0049: ldarg.3
IL_004a: ldloc.0
IL_004b: callvirt instance void GalaSoft.MvvmLight.ViewModelBase::Broadcast<!!T>(!!0, !!0, string)
		 ldc.i4.789
		 stloc.1
		 br.s IL_0020

		 // Block 789
IL_0054: ret
```

There different variations how you can implement this technique.
Since it is important that value of control variable to be presented on stack at the beginning of dispatcher block, you may omit storing value into local variable and jump directly to `IL_0020`.

For example instead of :
```cil
ldc.i4.789
stloc.1
br.s IL_0020
```
you can do:
```cil
ldc.i4.789
br.s IL_0021
```
