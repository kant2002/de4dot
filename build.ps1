$ErrorActionPreference = 'Stop'

dotnet build -c Release -f net48 de4dot.netframework.sln
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Remove-Item Release\net48\*.pdb, Release\net48\*.xml, Release\net48\Test.Rename.*

dotnet publish -c Release -f net10.0 -o publish-net10.0 de4dot
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Remove-Item publish-net10.0\*.pdb, publish-net10.0\*.xml
