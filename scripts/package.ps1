[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$publishDirectory = Join-Path $repositoryRoot 'dist/IPKeep'
$releaseDirectory = Join-Path $repositoryRoot 'dist/releases'
[xml]$properties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid product version.' }
foreach ($required in @('IPKeep.exe', 'IPKeep.dll', 'IPKeep.Core.dll', 'service/IPKeep.Service.exe', 'service/IPKeep.Service.dll', 'service/IPKeep.Core.dll', 'LICENSE', 'README.md', 'PRIVACY.md', 'CODE_SIGNING.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required) -PathType Leaf)) { throw "Missing published file: $required" }
}
$forbiddenFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | Where-Object {
    $_.Name -match '^(token\.bin|connection\.bin|settings\.json|status\.json|\.env.*)$|\.log(\.\d+)?$' -or
    $_.Extension -in @('.pfx', '.p12', '.pem', '.key', '.token', '.log')
})
if ($forbiddenFiles.Count -ne 0) { throw 'The publish directory contains private/runtime files.' }
[ordered]@{
    product = 'IPKeep'
    version = $version
    sourceCommit = $Commit
    repository = 'https://github.com/calxibe/ipkeep-windows'
    runtime = 'win-x64'
    signingStatus = 'unsigned'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishDirectory 'build-info.json') -Encoding utf8NoBOM
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$archiveName = "IPKeep-$version-windows-x64-unsigned.zip"
$archivePath = Join-Path $releaseDirectory $archiveName
if (Test-Path -LiteralPath $archivePath) { throw 'This preview archive already exists. Package from a fresh checkout.' }
Compress-Archive -LiteralPath $publishDirectory -DestinationPath $archivePath -CompressionLevel Optimal
$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $archiveName" | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Packaged $archiveName (SHA-256: $checksum)."
