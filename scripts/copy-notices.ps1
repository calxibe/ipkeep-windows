param([string]$PublishDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist/IPKeep'))
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$noticeRoot = Join-Path $PublishDirectory 'third-party'
New-Item -ItemType Directory -Path $noticeRoot -Force | Out-Null
$resolvedPackages = @{}
$packageRoots = @()
foreach ($project in @('IPKeep.Desktop', 'IPKeep.Service')) {
    $assets = Get-Content -LiteralPath (Join-Path $repositoryRoot "src/$project/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
    $packageRoots += @($assets.packageFolders.Keys)
    foreach ($key in $assets.libraries.Keys) {
        if ($assets.libraries[$key].type -eq 'package') { $resolvedPackages[$key] = $assets.libraries[$key].path }
    }
}
# Self-contained runtime packs are not always listed as ordinary package libraries.
$runtime = Get-Content -LiteralPath (Join-Path $PublishDirectory 'IPKeep.runtimeconfig.json') -Raw | ConvertFrom-Json
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $key = "$($framework.name).Runtime.win-x64/$($framework.version)"
    $resolvedPackages[$key] = $key.ToLowerInvariant()
}
$index = [Collections.Generic.List[string]]::new()
$index.Add('# Bundled dependency notices')
$index.Add('')
$index.Add('IPKeep source is MIT-licensed. Third-party components retain their own licenses. The Windows App SDK redistributables are covered by Microsoft license terms, not by the IPKeep MIT license. This inventory includes restored build dependencies as well as runtime dependencies; not every package is included in the application.')
$index.Add('')
foreach ($key in ($resolvedPackages.Keys | Sort-Object)) {
    $packageDirectory = $null
    foreach ($root in ($packageRoots | Select-Object -Unique)) {
        $candidate = Join-Path $root $resolvedPackages[$key]
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory = $candidate; break }
    }
    if ($null -eq $packageDirectory) { throw "Cannot find restored dependency notices: $key" }
    $nuspec = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File)[0]
    [xml]$metadata = Get-Content -LiteralPath $nuspec.FullName -Raw
    $licenseNode = $metadata.package.metadata.license
    $licenseText = if ($null -ne $licenseNode) { $licenseNode.InnerText } else { [string]$metadata.package.metadata.licenseUrl }
    $index.Add("## $key")
    $index.Add('')
    $index.Add("NuGet license metadata: $licenseText")
    $index.Add('')
    $destination = Join-Path $noticeRoot $key
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object Name -Match 'license|third.?party|notice')) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination $file.Name) -Force
        $index.Add("- [$($file.Name)]($key/$($file.Name))")
    }
    if ($licenseNode.type -eq 'file' -and -not (Test-Path -LiteralPath (Join-Path $destination $licenseText))) {
        $licenseSource = Join-Path $packageDirectory $licenseText
        $licenseDestination = Join-Path $destination $licenseText
        New-Item -ItemType Directory -Path (Split-Path $licenseDestination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $licenseSource -Destination $licenseDestination -Force
        $index.Add("- [License file]($key/$licenseText)")
    }
    $index.Add('')
}
$index | Set-Content -LiteralPath (Join-Path $noticeRoot 'README.md') -Encoding utf8NoBOM
Write-Output "Included license metadata/notices for $($resolvedPackages.Count) restored dependencies."
