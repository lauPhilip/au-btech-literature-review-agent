# Windows twin of tools/test-scope.sh: runs that script, with the same arguments, in the bash that comes with
# Git for Windows, so there is one map of folders to tests. Examples:
#   .\tools\test-scope.ps1                 prints ALL, NONE or the filter for origin/master...HEAD and your edits
#   .\tools\test-scope.ps1 --run           also runs those tests
#   .\tools\test-scope.ps1 --run -- --no-build
#   .\tools\test-scope.ps1 --base origin/feature-x   measures from the merge-base of that ref and HEAD, so a long-lived
#                                                     branch keeps the scope of its own work (default: origin/master)
$git = (Get-Command git -ErrorAction Stop).Source
$bash = Join-Path (Split-Path (Split-Path $git)) 'bin\bash.exe'
if (-not (Test-Path $bash)) { $bash = (Get-Command bash -ErrorAction Stop).Source }
& $bash ((Join-Path $PSScriptRoot 'test-scope.sh') -replace '\\', '/') @args
exit $LASTEXITCODE
