# fuaran-survey-sav — "drop into the repo, run one command, the thing works".
# Stage-0 shape (library + tests only): tool restore -> fantomas -> build -> Expecto runner.
#
#   pwsh ./run.ps1                 full pass
#   pwsh ./run.ps1 -SkipFormat     skip the Fantomas pass
#   pwsh ./run.ps1 -SkipBuild      skip the build (implies a prior build)
#   pwsh ./run.ps1 -SkipTests      skip the Expecto suite
#   pwsh ./run.ps1 -Regenerate     rewrite the committed fixtures first (needs Python 3)
#
# The fixtures under tests/fixtures are committed, so the gate itself never needs Python;
# -Regenerate is the deliberate act of rewriting them, reviewed as a diff.
#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $SkipFormat,
    [switch] $SkipBuild,
    [switch] $SkipTests,
    [switch] $Regenerate
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# A stage that runs no native command must not inherit another stage's exit code, and the
# `global:` scope is what reaches the code a caller sees when this script is invoked with `&`.
$global:LASTEXITCODE = 0

$solution = 'Fuaran.Survey.Sav.slnx'
$testProject = 'tests/Fuaran.Survey.Sav.Tests'

if ($Regenerate) {
    & (Join-Path $PSScriptRoot 'tests/fixtures/regenerate.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$global:LASTEXITCODE = 0
if (-not $SkipFormat) {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet fantomas src tests
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$global:LASTEXITCODE = 0
if (-not $SkipBuild) {
    dotnet build $solution --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$global:LASTEXITCODE = 0
if (-not $SkipTests) {
    # `--no-build` only when this run already built; otherwise let `dotnet run` build first.
    if ($SkipBuild) {
        dotnet run --project $testProject
    }
    else {
        dotnet run --project $testProject --no-build
    }
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
