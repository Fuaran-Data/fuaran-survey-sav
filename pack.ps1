# Pack Fuaran.Survey.Sav into a local folder feed — by default the shared feed three directories up from
# this repository, the layout it is developed in. Publication to nuget.org is NOT this script: it is a `v*`
# tag (DECISIONS.md D8).
#
#   pwsh ./pack.ps1                      pack at the version in Directory.Build.props
#   pwsh ./pack.ps1 -Configuration Debug
#   pwsh ./pack.ps1 -Feed <dir>          pack into another folder
#   pwsh ./pack.ps1 -QuietRepack         overwrite an already-packed version deliberately
#   pwsh ./pack.ps1 -Sweep               the caller is a multi-package sweep that keeps its own fingerprint
#
# A folder feed keys a package by id AND version, so packing a version that is already there rewrites the
# slot under every consumer pinned to it while their extracted caches keep serving the old bits. This script
# therefore refuses when the slot exists, unless -QuietRepack or -Sweep says the caller meant it.
#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [switch] $QuietRepack,
    [switch] $Sweep,
    [string] $Feed
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$global:LASTEXITCODE = 0

$feed = if ($Feed) { $Feed } else { Join-Path $PSScriptRoot '..\..\..\local-nuget-feed' }
if (-not (Test-Path $feed)) { New-Item -ItemType Directory -Force $feed | Out-Null }
$feed = (Resolve-Path $feed).Path

[xml] $props = Get-Content 'Directory.Build.props'
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'Directory.Build.props declares no <Version>.' }

$slot = Join-Path $feed "Fuaran.Survey.Sav.$version.nupkg"
if ((Test-Path $slot) -and -not $QuietRepack -and -not $Sweep) {
    Write-Error ("$slot already exists: advance <Version> in Directory.Build.props, or pass -QuietRepack to " +
        'overwrite this one slot deliberately.')
    exit 1
}
if ((Test-Path $slot) -and $Sweep) { Write-Host "pack: repacking $slot (sweep)" }

dotnet pack 'src/Fuaran.Survey.Sav/Fuaran.Survey.Sav.fsproj' -c $Configuration -o $feed --nologo
exit $LASTEXITCODE
