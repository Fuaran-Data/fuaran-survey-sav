# Rewrites the committed SPSS fixtures and expected renderings in this folder from gen_sav.py.
# The gate never runs this: the generated files are committed (DECISIONS.md D3), so regenerating
# is a deliberate act whose result is reviewed as a diff.
#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$python = Get-Command python3, python, py -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $python) {
    Write-Error "Python 3 is needed to regenerate the fixtures (standard library only); the committed fixtures are still valid without it."
}

$global:LASTEXITCODE = 0
& $python.Source -I (Join-Path $PSScriptRoot 'gen_sav.py') $PSScriptRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
