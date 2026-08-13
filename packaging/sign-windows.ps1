param(
    [Parameter(Mandatory = $true)][string]$Path,
    [switch]$Required
)

$ErrorActionPreference = 'Stop'

$certificate = $env:ABRAXIUS_WINDOWS_CERTIFICATE_BASE64
$password = $env:ABRAXIUS_WINDOWS_CERTIFICATE_PASSWORD
$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if ([string]::IsNullOrWhiteSpace($certificate) -or [string]::IsNullOrWhiteSpace($password) -or $null -eq $signtool) {
    if ($Required) { throw 'Production Windows signing requires signtool and the configured certificate secrets.' }
    Write-Host 'Windows signing skipped: signing credentials/tool are not configured.'
    exit 0
}

$certificatePath = Join-Path $env:RUNNER_TEMP 'abraxius-signing.pfx'
[Convert]::FromBase64String($certificate) | Set-Content -Path $certificatePath -AsByteStream
try {
    Get-ChildItem -Path $Path -Recurse -File | Where-Object { $_.Extension -in '.exe', '.dll' } | ForEach-Object {
        & $signtool.Source sign /fd SHA256 /f $certificatePath /p $password /tr http://timestamp.digicert.com /td SHA256 $_.FullName
    }
}
finally {
    Remove-Item $certificatePath -Force -ErrorAction SilentlyContinue
}
