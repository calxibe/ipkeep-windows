# Read-only diagnostics for the service's recurring installation/connection checks.
# Run in an administrator PowerShell on the affected computer. No token is read.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$administrator = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { [Environment]::GetFolderPath('ProgramFiles') }
$installRoot = Join-Path $programFiles 'IPKeep'
$dataRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'IPKeep'
$adminSid = 'S-1-5-32-544'
$systemSid = 'S-1-5-18'
$serviceSid = 'S-1-5-19'
$usersSid = 'S-1-5-32-545'
$writeRights = [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor
    [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
    [Security.AccessControl.FileSystemRights]::TakeOwnership

function PrincipalLabel([string]$sid) {
    switch ($sid) {
        $adminSid { 'Administrators' }
        $systemSid { 'SYSTEM' }
        $serviceSid { 'LocalService' }
        $usersSid { 'Users' }
        'S-1-1-0' { 'Everyone' }
        'S-1-5-11' { 'Authenticated Users' }
        default { 'Other account' }
    }
}

Write-Output 'IPKeep permission diagnostics (read-only; no token contents)'
Write-Output "Administrator: $administrator"
if (-not $administrator) { Write-Output 'Run again as administrator: the Private directory normally denies access to a standard user.' }
try {
    $service = Get-CimInstance Win32_Service -Filter "Name = 'IPKeep'"
    if ($null -eq $service) { Write-Output 'Service: not installed' }
    else {
        $expectedExecutable = '"' + (Join-Path $installRoot 'service\IPKeep.Service.exe') + '"'
        Write-Output "Service: $($service.State); LocalService account: $($service.StartName -ieq 'NT AUTHORITY\LocalService'); expected executable: $($service.PathName -ieq $expectedExecutable)"
    }
} catch { Write-Output "Service query failed: $($_.Exception.GetType().Name)" }

$targets = @(
    @{ Label = 'Install folder'; Path = $installRoot },
    @{ Label = 'Service folder'; Path = Join-Path $installRoot 'service' },
    @{ Label = 'Service executable'; Path = Join-Path $installRoot 'service\IPKeep.Service.exe' },
    @{ Label = 'Data folder'; Path = $dataRoot },
    @{ Label = 'Private folder'; Path = Join-Path $dataRoot 'Private'; Secret = $true },
    @{ Label = 'Saved connection'; Path = Join-Path $dataRoot 'Private\connection.bin'; Secret = $true },
    @{ Label = 'Activity folder'; Path = Join-Path $dataRoot 'Activity'; Runtime = $true }
)
foreach ($target in $targets) {
    Write-Output "--- $($target.Label) ---"
    try {
        $item = Get-Item -LiteralPath $target.Path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Write-Output 'FAIL: link or junction is not supported.'
            continue
        }
        $acl = Get-Acl -LiteralPath $target.Path
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        $ownerAllowed = $owner -in @($adminSid, $systemSid) -or ($target.Runtime -and $owner -eq $serviceSid)
        Write-Output "Owner: $(PrincipalLabel $owner); accepted by service: $ownerAllowed"
        $unsafe = 0
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            $sid = $rule.IdentityReference.Value
            $rights = $rule.FileSystemRights
            Write-Output "  $(PrincipalLabel $sid): $($rule.AccessControlType) $rights; propagation=$($rule.PropagationFlags)"
            if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $sid -in @($adminSid, $systemSid)) { continue }
            if ($sid -eq $serviceSid -and $target.Runtime) { continue }
            if (($rights -band $writeRights) -ne 0 -or ([int]$rights -band 0x50000000) -ne 0 -or
                ($target.Secret -and $sid -ne $serviceSid -and ($rights -band [Security.AccessControl.FileSystemRights]::ReadData) -ne 0)) { $unsafe++ }
        }
        Write-Output "Permission rules rejected by service: $unsafe"
    } catch {
        Write-Output "Cannot inspect: $($_.Exception.GetType().Name); HRESULT=$($_.Exception.HResult)"
    }
}
Write-Output 'Done. No settings, files, permissions or services were changed.'
