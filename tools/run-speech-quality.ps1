param([Parameter(Mandatory=$true)][string]$Executable,[Parameter(Mandatory=$true)][string]$Output,[string]$Cases='',[ValidateSet('legacy','phase1')][string]$Pipeline='legacy')
$ErrorActionPreference='Stop'
if (!$Cases) { $Cases=Join-Path $PSScriptRoot 'speech-quality-cases.json' }
$taskExe=(Resolve-Path -LiteralPath $Executable).Path
$taskOutput=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskOutput) | Out-Null
$taskCases=(Resolve-Path -LiteralPath $Cases).Path
$taskArguments='--speech-quality-test --cases "'+$taskCases+'" --output "'+$taskOutput+'" --pipeline '+$Pipeline
$taskProcess=Start-Process -FilePath $taskExe -ArgumentList $taskArguments -WindowStyle Hidden -PassThru
try {
    if (!$taskProcess.WaitForExit(7200000)) { $taskProcess.Kill();throw 'Speech quality run timed out.' }
    if ($taskProcess.ExitCode -ne 0) { throw 'Speech quality executable failed.' }
    if (!(Test-Path -LiteralPath $taskOutput)) { throw 'Speech quality report missing.' }
    $taskReport=Get-Content -LiteralPath $taskOutput -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$taskReport.complete -or $taskReport.cases.Count -eq 0) { throw 'Incomplete speech quality report.' }
    Write-Output "PASS: $($taskReport.cases.Count) cases measured; failures remain in report: $taskOutput"
} finally { $taskProcess.Dispose() }
