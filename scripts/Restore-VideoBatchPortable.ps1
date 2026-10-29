#Requires -Version 5.1
param([switch]$Force, [switch]$Quiet)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot.TrimEnd('\')
$App = Join-Path $Root 'app'

function Normalize-PathLiteral([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) { return '' }
    try { return [IO.Path]::GetFullPath($path.TrimEnd('\', '/')) } catch { return $path.TrimEnd('\', '/') }
}
$App = Normalize-PathLiteral $App
$UploadLocal = Join-Path $Root 'upload-staging'
$VideosLocal = Join-Path $Root 'videos\VideoBatch'
$UserStaging = Join-Path $Root 'user-data'
$TargetData = Join-Path $env:LOCALAPPDATA 'VideoBatchDesktop'
$TargetVideos = Join-Path $env:USERPROFILE 'Videos\VideoBatch'

if (-not (Test-Path -LiteralPath (Join-Path $App 'VideoBatch.exe'))) {
    throw 'VideoBatch.exe not found in app\. Extract the full archive first.'
}

$manifestPath = Join-Path $Root 'portable-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'portable-manifest.json missing.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json

$leaf = [string]$manifest.pathMap.materialsLeaf
if ([string]::IsNullOrWhiteSpace($leaf)) { $leaf = (Get-ChildItem -LiteralPath (Join-Path $Root 'materials') -Directory | Select-Object -First 1).Name }
$Materials = Join-Path $Root (Join-Path 'materials' $leaf)

$oldApp = [string]$manifest.pathMap.oldApp
$oldMaterials = [string]$manifest.pathMap.oldMaterials
$oldUser = [string]$manifest.pathMap.oldUserProfile
$oldUpload = [string]$manifest.pathMap.oldUploadStaging
$newUser = $env:USERPROFILE

function Replace-InFiles {
    param([string[]]$Roots, [hashtable]$Map)
    $ext = @('*.xml', '*.json', '*.jsonl', '*.txt')
    foreach ($r in $Roots) {
        if (-not (Test-Path -LiteralPath $r)) { continue }
        foreach ($pattern in $ext) {
            Get-ChildItem -LiteralPath $r -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
                $raw = [IO.File]::ReadAllText($_.FullName)
                $next = $raw
                foreach ($k in $Map.Keys) {
                    if ($k -and $Map[$k]) { $next = $next.Replace($k, [string]$Map[$k]) }
                }
                if ($next -ne $raw) {
                    [IO.File]::WriteAllText($_.FullName, $next, [Text.UTF8Encoding]::new($false))
                }
            }
        }
    }
}

$map = @{
    $oldApp = $App
    $oldMaterials = $Materials
    $oldUser = $newUser
    $oldUpload = $UploadLocal
    'C:\Users\hackerman' = $newUser
}

$installRootFile = Join-Path $TargetData 'install-root.txt'
if (Test-Path -LiteralPath $installRootFile) {
    $linkedApp = Normalize-PathLiteral ([IO.File]::ReadAllText($installRootFile).Trim())
    if ($linkedApp -eq $App) {
        if (-not $Quiet) { Write-Host "OK: already configured for this folder." }
        exit 0
    }
    $Force = $true
}

if (-not $Quiet) { Write-Host "Rewriting paths in staged user-data..." }
Replace-InFiles -Roots @($UserStaging) -Map $map

if (Test-Path -LiteralPath $TargetData) {
    if (-not $Force) {
        throw "Already exists: $TargetData. Close VideoBatch and run Launch-VideoBatch-Portable.bat again."
    }
    $bak = "$TargetData.before-portable-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    if (-not $Quiet) { Write-Host "Backup existing data -> $bak" }
    Move-Item -LiteralPath $TargetData -Destination $bak
}

if (-not $Quiet) { Write-Host "Installing user data -> $TargetData" }
New-Item -ItemType Directory -Force -Path $TargetData | Out-Null
& robocopy $UserStaging $TargetData /E /COPY:DAT /R:2 /W:2 /NFL /NDL /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy user-data failed ($LASTEXITCODE)" }

Replace-InFiles -Roots @($TargetData) -Map $map

[IO.File]::WriteAllText($installRootFile, $App, [Text.UTF8Encoding]::new($false))

if (Test-Path -LiteralPath $VideosLocal) {
    if (-not $Quiet) { Write-Host "Merging videos -> $TargetVideos" }
    New-Item -ItemType Directory -Force -Path $TargetVideos | Out-Null
    & robocopy $VideosLocal $TargetVideos /E /COPY:DAT /R:2 /W:2 /NFL /NDL /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy videos failed ($LASTEXITCODE)" }
    Replace-InFiles -Roots @($TargetData) -Map @{ $VideosLocal = $TargetVideos }
}

if (-not $Quiet) {
    Write-Host ""
    Write-Host "Done. Launch: $Root\Launch-VideoBatch-Portable.bat"
}
