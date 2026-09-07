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
    foreach ($document in @('README.md', 'LICENSE', 'PRIVACY.md', 'CODE_SIGNING.md')) {
        Copy-Item -LiteralPath $document -Destination (Join-Path 'dist/IPKeep' $document)
    }
    New-Item -ItemType Directory -Path dist/IPKeep/docs/images -Force | Out-Null
    foreach ($preview in @('overview.png', 'settings.png', 'activity.png')) {
        Copy-Item -LiteralPath (Join-Path 'docs/images' $preview) -Destination (Join-Path 'dist/IPKeep/docs/images' $preview)
    }
    Write-Host "Built $PSScriptRoot\dist\IPKeep\IPKeep.exe"
    Write-Host 'Keep the complete folder together. Building does not install or start a service.'
}
finally { Pop-Location }
