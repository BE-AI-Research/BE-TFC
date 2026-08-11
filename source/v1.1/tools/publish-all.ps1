<#
.SYNOPSIS
  Publish portable BE-TFC executables for every supported architecture.

.DESCRIPTION
  Emits one self-contained single-file exe per architecture into dist\, named
  so a tech can tell them apart on a USB stick without launching them:

      dist\BE-TFC-1.2.0-x64.exe
      dist\BE-TFC-1.2.0-arm64.exe

  Windows 11 on ARM runs the x64 build under emulation, so arm64 is an
  optimisation rather than a requirement — but scanning is exactly the
  filesystem-bound workload where emulation hurts.

.PARAMETER Arch
  Architectures to build. Defaults to both.

.PARAMETER Aot
  Use the experimental NativeAOT publish (~20 MB instead of ~90 MB).
  WinForms AOT is experimental in .NET 9 — verify the GUI before shipping one.

.EXAMPLE
  .\tools\publish-all.ps1
  .\tools\publish-all.ps1 -Arch win-arm64
  .\tools\publish-all.ps1 -Aot
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Arch = @('win-x64', 'win-arm64'),

    [switch] $Aot
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $repoRoot 'src\BETFC.csproj'
$distDir  = Join-Path $repoRoot 'dist'

if (-not (Test-Path $project)) { throw "Project not found: $project" }
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# Read <Version> straight from the csproj so the filenames cannot drift from it.
[xml] $csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw 'Could not read <Version> from BETFC.csproj' }

Write-Host "BE-TFC $version — publishing: $($Arch -join ', ')" -ForegroundColor Cyan

if ($Aot) {
    Write-Host 'NativeAOT enabled (experimental for WinForms)' -ForegroundColor Yellow

    # The ILC targets shell out to vswhere.exe to locate the MSVC linker, but do
    # not qualify the path. On a machine where the VS Installer directory is not
    # on PATH the link step dies with "'vswhere.exe' is not recognized" — which
    # reads like a missing C++ toolchain when the toolchain is in fact present.
    $vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (Test-Path (Join-Path $vsInstaller 'vswhere.exe')) {
        if ($env:PATH -notlike "*$vsInstaller*") {
            $env:PATH = "$vsInstaller;$env:PATH"
            Write-Host "   added to PATH for this run: $vsInstaller" -ForegroundColor DarkGray
        }
    }
    else {
        Write-Warning "vswhere.exe not found under '$vsInstaller'. NativeAOT needs the " +
                      'Visual Studio C++ build tools (Desktop development with C++).'
    }
}

$results = @()

foreach ($rid in $Arch) {
    $short = $rid -replace '^win-', ''
    Write-Host ''
    Write-Host "── $rid" -ForegroundColor Cyan

    # Separate output directory per variant. AOT and standard publishes would
    # otherwise share bin\...\<rid>\publish\, and because an incremental publish
    # can decide the output is up to date, a leftover AOT exe gets copied out and
    # shipped as the standard build — an experimental binary wearing the verified
    # build's filename. Wiped first so nothing stale can survive either.
    $variant    = if ($Aot) { 'aot' } else { 'std' }
    $publishDir = Join-Path $repoRoot "src\bin\Release\net9.0-windows\$rid\publish-$variant"
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    # Drop the RID's intermediate output so everything is genuinely recompiled,
    # including the generated assembly-info source that carries <Version>.
    #
    # MSBuild decides what to rebuild by comparing timestamps, which makes it
    # quietly wrong whenever the system clock moves backwards (VM resume, a
    # timezone or NTP correction). Outputs then sit in the future relative to
    # freshly edited sources, so a real change — a version bump, a new file —
    # looks up to date and is skipped. That failure is silent and ships a stale
    # binary under a new name. A release build should not depend on clock
    # sanity. Targeted at the RID dir so project.assets.json survives and no
    # full restore is needed. (dotnet publish has no --no-incremental.)
    $objDir = Join-Path $repoRoot "src\obj\Release\net9.0-windows\$rid"
    if (Test-Path $objDir) { Remove-Item $objDir -Recurse -Force }

    $publishArgs = @(
        'publish', $project,
        '-c', 'Release',
        "-p:TargetArch=$rid",
        "-p:PublishDir=$publishDir\",
        '--nologo'
    )
    if ($Aot) { $publishArgs += '-p:UseAot=true' }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid (exit $LASTEXITCODE)" }

    $built = Join-Path $publishDir 'BE-TFC.exe'
    if (-not (Test-Path $built)) { throw "Expected output missing: $built" }

    # Assert the binary really is the version we are about to stamp on the
    # filename. A publish that silently no-ops would otherwise ship an old exe
    # under a new name, and the checksum file would vouch for it.
    $builtVersion = (Get-Item $built).VersionInfo.ProductVersion
    if ($builtVersion -ne $version) {
        throw "version mismatch for ${rid}: csproj says $version, built exe reports $builtVersion"
    }

    $suffix = if ($Aot) { "-$short-aot" } else { "-$short" }
    $dest   = Join-Path $distDir "BE-TFC-$version$suffix.exe"
    Copy-Item $built $dest -Force

    $sizeMb = [math]::Round((Get-Item $dest).Length / 1MB, 1)
    $hash   = (Get-FileHash $dest -Algorithm SHA256).Hash

    $results += [pscustomobject]@{
        Arch   = $short
        File   = Split-Path $dest -Leaf
        SizeMB = $sizeMb
        SHA256 = $hash
    }
    Write-Host "   → $dest ($sizeMb MB)" -ForegroundColor Green
}

Write-Host ''
$results | Format-Table -AutoSize

# A checksum file travels with the build: a portable exe handed around on a USB
# stick has no installer and no signature chain to vouch for it. Named per
# variant so an -Aot run cannot silently overwrite the standard build's sums.
$sumSuffix = if ($Aot) { '-aot' } else { '' }
$sumFile = Join-Path $distDir "BE-TFC-$version$sumSuffix-SHA256SUMS.txt"
$results | ForEach-Object { "$($_.SHA256)  $($_.File)" } | Set-Content $sumFile -Encoding ascii
Write-Host "Checksums: $sumFile" -ForegroundColor Cyan
