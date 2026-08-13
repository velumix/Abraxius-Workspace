$ErrorActionPreference = 'Stop'

param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [string]$Version = '',
    [ValidateSet('stable', 'beta', 'development')][string]$Channel = 'stable'
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } else { 'dotnet' }
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$versionProps = Get-Content (Join-Path $repoRoot 'build/Version.props')
    $Version = $versionProps.Project.PropertyGroup.AbraxiusVersion
}

switch ($Rid) {
    'win-x64' { $runtime = 'win-x64'; $mainExe = 'Abraxius.Desktop.exe' }
    'win-arm64' { $runtime = 'win-arm64'; $mainExe = 'Abraxius.Desktop.exe' }
    default { throw "Unsupported Windows RID: $Rid" }
}

$stage = Join-Path $repoRoot "artifacts/staging/$Rid"
$output = Join-Path $repoRoot "artifacts/releases/$Rid"
Remove-Item $stage, $output -Recurse -Force -ErrorAction SilentlyContinue
New-Item $stage, $output -ItemType Directory -Force | Out-Null

& $dotnet restore (Join-Path $repoRoot 'Abraxius.sln')
& $dotnet publish (Join-Path $repoRoot 'src/Abraxius.App.Desktop/Abraxius.App.Desktop.csproj') `
    --configuration Release --runtime $runtime --self-contained true --output $stage `
    "-p:AbraxiusVersion=$Version" "-p:AbraxiusReleaseChannel=$Channel" `
    "-p:AbraxiusGitCommit=$($env:GITHUB_SHA ?? 'unknown')" `
    "-p:AbraxiusBuildTimestamp=$([DateTime]::UtcNow.ToString('o'))"

if (-not (Test-Path (Join-Path $stage $mainExe))) { throw "Published executable was not found." }
if ($env:ABRAXIUS_REQUIRE_SIGNING -eq 'true') {
    & (Join-Path $repoRoot 'packaging/sign-windows.ps1') -Path $stage -Required
}
Push-Location $repoRoot
try {
    & $dotnet tool restore
    $packArgs = @('--packId', 'Abraxius', '--packTitle', 'Abraxius', '--packVersion', $Version,
        '--packDir', $stage, '--mainExe', $mainExe, '--outputDir', $output, '--channel', $Channel, '--runtime', $runtime)
    if ($env:ABRAXIUS_ICON_PATH) { $packArgs += @('--icon', $env:ABRAXIUS_ICON_PATH) }
    & $dotnet vpk pack @packArgs
}
finally { Pop-Location }

Get-ChildItem $output -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
} | Set-Content (Join-Path $output 'SHA256SUMS.txt')
Write-Host "Created $Rid packages in $output"
