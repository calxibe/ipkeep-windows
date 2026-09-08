[CmdletBinding()]
param(
    [string]$PublishDirectory = 'dist/IPKeep',
    [string]$OutputDirectory = 'dist/releases',
    [ValidateRange(1, 65535)][int]$BetaNumber = 2,
    [string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repositoryRoot
try {
    $publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $output = (Resolve-Path -LiteralPath $OutputDirectory).Path
    if (-not $CompilerPath) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $CompilerPath = $command.Source }
        else { $CompilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
    }
    if (-not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
        throw 'Inno Setup 6.7 or later is required. Install it from https://jrsoftware.org/isdl.php or use the GitHub Windows build.'
    }
    $required = @('IPKeep.exe','IPKeep.dll','service/IPKeep.Service.exe','service/IPKeep.Service.dll','Assets/IPKeep.ico','LICENSE','PRIVACY.md','CODE_SIGNING.md','third-party/README.md','build-info.json')
    foreach ($file in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $publish $file) -PathType Leaf)) { throw "Missing published file: $file" }
    }
    $privateFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {
        $_.Name -match '^(token\.bin|connection\.bin|settings\.json|status\.json|\.env.*)$|\.log(\.\d+)?$' -or
        $_.Extension -in @('.pfx','.p12','.pem','.key','.token','.log')
    })
    if ($privateFiles.Count) { throw 'The published folder contains private/runtime files.' }
    [xml]$properties = Get-Content -LiteralPath Directory.Build.props -Raw
    $version = [string]$properties.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid product version.' }
    $installerName = "IPKeep-$version-preview.$BetaNumber-windows-x64-unsigned-setup.exe"
    $installer = Join-Path $output $installerName
    if (Test-Path -LiteralPath $installer) { throw 'The installer already exists. Use a new output directory or beta number.' }
    & $CompilerPath /Qp "/DPublishDir=$publish" "/DOutputDir=$output" "/DProductVersion=$version" "/DBetaNumber=$BetaNumber" (Join-Path $repositoryRoot 'installer/IPKeep.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    if ((Get-AuthenticodeSignature -LiteralPath $installer).Status -ne 'NotSigned') { throw 'Expected an explicitly unsigned beta installer.' }
    $checksum = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    "$checksum  $installerName" | Add-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "Built $installer (SHA-256: $checksum)."
}
finally { Pop-Location }
