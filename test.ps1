$Configuration="Release"

mkdir obj -Force | Out-Null
# C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\x64
$env:PATH="C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\x64;c:\Windows\Microsoft.NET\Framework64\v4.0.30319\;$env:PATH"

$de4dotExec="Release\net8.0\win-x64\de4dot.exe"

function Test($item)
{
	# tests\samples\inlining\inline_static_method_new.il
	$base = [System.IO.Path]::GetFileNameWithoutExtension($item)
	$dir = Split-Path $item
	mkdir -Force obj\$dir | Out-Null
	ilasm /DLL /QUIET "$item" /OUTPUT=obj\$dir\$base.dll

	& $de4dotExec  obj\$dir\$base.dll -o obj\$dir\$base.cleaned.dll

	ildasm /NOBAR obj\$dir\$base.cleaned.dll /OUT=$dir\$base.cleaned.il
}

Test("tests\samples\inlining\inline_static_method.il")
Test("tests\samples\inlining\inline_static_generic_method.il")
Test("tests\samples\inlining\inline_static_method_br_target.il")
Test("tests\samples\inlining\inline_static_method_new.il")