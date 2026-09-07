[CmdletBinding()]
param([switch]$NoTest)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Push-Location $PSScriptRoot
try {
    if (-not $NoTest) {
        dotnet run --project tests/IPKeep.Tests/IPKeep.Tests.csproj -c Release
        if ($LASTEXITCODE -ne 0) { throw 'IPKeep tests failed.' }
    }
    dotnet publish src/IPKeep.Desktop/IPKeep.Desktop.csproj -c Release -r win-x64 --self-contained true -o dist/IPKeep
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    dotnet publish src/IPKeep.Service/IPKeep.Service.csproj -c Release -r win-x64 --self-contained true -o dist/IPKeep/service
    if ($LASTEXITCODE -ne 0) { throw 'Service build failed.' }
    Copy-Item -LiteralPath README.md -Destination dist/IPKeep/README.md
    Write-Host "Built $PSScriptRoot\dist\IPKeep\IPKeep.exe"
    Write-Host 'Keep the complete folder together. Building does not install or start a service.'
}
finally { Pop-Location }
