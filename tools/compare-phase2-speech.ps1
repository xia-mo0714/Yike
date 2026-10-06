param([Parameter(Mandatory=$true)][string]$Baseline,[Parameter(Mandatory=$true)][string]$Candidate,[Parameter(Mandatory=$true)][string]$Output,[string]$Executable='')
$ErrorActionPreference='Stop'
if(!$Executable){$Executable=Join-Path $PSScriptRoot '../dist-phase2/Yike.exe'}
Add-Type -AssemblyName System.Web.Extensions
$taskAssembly=[Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
$taskType=$taskAssembly.GetType('WindowsTranslator.PhaseTwoAcceptance',$true)
$taskFlags=[Reflection.BindingFlags]'Static,NonPublic'
$taskSourcePath=Join-Path $PSScriptRoot '../tests/SpeechQualityTests.cs'
$taskScorerArguments=New-Object object[] 1
$taskScorerArguments[0]=$taskSourcePath.PSObject.BaseObject
$taskScorer=$taskType.GetMethod('VerifyScorer',$taskFlags).Invoke($null,$taskScorerArguments)
$taskBaseline=(Resolve-Path -LiteralPath $Baseline).Path
$taskCandidate=(Resolve-Path -LiteralPath $Candidate).Path
$taskBaselineReport=Get-Content -LiteralPath $taskBaseline -Raw -Encoding UTF8 | ConvertFrom-Json
if($taskBaselineReport.pipeline -eq 'phase1' -and (Get-FileHash -LiteralPath $taskBaseline).Hash -ne '5590AEB9D7DB652A487B2D7E1182DA2AB98B9CD6C1522CC3F9866A19BD68017E'){throw 'Frozen phase-one report changed'}
if($taskBaselineReport.pipeline -eq 'phase2' -and $taskBaselineReport.scorerSha256 -ne $taskScorer){throw 'Scorer hash changed'}
$taskCompareArguments=New-Object object[] 3
$taskCompareArguments[0]=$taskBaseline.PSObject.BaseObject
$taskCompareArguments[1]=$taskCandidate.PSObject.BaseObject
$taskCompareArguments[2]=$taskScorer.PSObject.BaseObject
$taskResult=$taskType.GetMethod('CompareQuality',$taskFlags).Invoke($null,$taskCompareArguments)
$taskSerializer=New-Object Web.Script.Serialization.JavaScriptSerializer
$taskJson=$taskSerializer.Serialize($taskResult.PSObject.BaseObject)
$taskOutput=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskOutput) | Out-Null
[IO.File]::WriteAllText($taskOutput,$taskJson,[Text.UTF8Encoding]::new($false))
Write-Output "Quality comparison: $($taskJson | ConvertFrom-Json | Select-Object -ExpandProperty status); $taskOutput"
