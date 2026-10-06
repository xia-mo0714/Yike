param([string]$OutputDirectory='./dist-phase2',[string]$ReportDirectory='./work/phase2-quality',[ValidateSet('Full','Native','Quality','Latency','Cycles','Soak')][string]$Mode='Full')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskBuild=if([IO.Path]::IsPathRooted($OutputDirectory)){[IO.Path]::GetFullPath($OutputDirectory)}else{[IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))}
$taskReports=if([IO.Path]::IsPathRooted($ReportDirectory)){[IO.Path]::GetFullPath($ReportDirectory)}else{[IO.Path]::GetFullPath((Join-Path $taskRoot $ReportDirectory))}
New-Item -ItemType Directory -Force -Path $taskReports | Out-Null
$taskExe=Join-Path $taskBuild 'Yike.exe'
function Invoke-PhaseTwoTool([string]$Name,[string]$File,[string]$Arguments,[int]$DeadlineSeconds=120){
 $taskStarted=Get-Date
 $taskProcess=Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskReports ($Name+'.log')) -RedirectStandardError (Join-Path $taskReports ($Name+'.stderr.log'))
 $null=$taskProcess.Handle
 try{while(!$taskProcess.WaitForExit(5000)){if(((Get-Date)-$taskStarted).TotalSeconds -gt $DeadlineSeconds){$taskProcess.Kill();throw "Tool timed out: $Name"}}$taskProcess.WaitForExit();if($taskProcess.ExitCode -ne 0){throw "Tool failed: $Name exit=$($taskProcess.ExitCode) (see local logs)"}}finally{$taskProcess.Dispose()}
}
function Invoke-PhaseTwoQa([string]$Name,[string]$Arguments,[int]$DeadlineSeconds){
 $taskReport=Join-Path $taskReports ($Name+'.json')
 $taskStarted=Get-Date
 $taskProcess=Start-Process -FilePath $taskExe -ArgumentList ($Arguments+' --output "'+$taskReport+'"') -WindowStyle Hidden -PassThru
 $null=$taskProcess.Handle
 try{
  while(!$taskProcess.WaitForExit(5000)){
   if(((Get-Date)-$taskStarted).TotalSeconds -gt $DeadlineSeconds){$taskProcess.Kill();throw "QA mode timed out: $Name"}
  }
  if($taskProcess.ExitCode -ne 0){throw "QA process failed: $Name (see local .error report)"}
  if(!(Test-Path -LiteralPath $taskReport)){throw "QA report missing: $Name"}
  $taskData=Get-Content -LiteralPath $taskReport -Raw -Encoding UTF8 | ConvertFrom-Json
  if(!$taskData.complete){throw "QA report incomplete: $Name"}
  Write-Output "Measured $Name; failures retained in $taskReport"
 }finally{$taskProcess.Dispose()}
}
if($Mode -in @('Full','Native')){
 & (Join-Path $PSScriptRoot 'build-speech-worker.ps1') -OutputDirectory $taskBuild -Test *> (Join-Path $taskReports 'worker-build.log')
 if($LASTEXITCODE -ne 0){throw 'Worker test build failed'}
 Invoke-PhaseTwoTool 'worker-tests' (Join-Path $taskBuild 'YikeSpeechWorker.Tests.exe') '--self-test'
 $taskRelativeBuild=[Uri]::UnescapeDataString(([Uri]($taskRoot.TrimEnd('\')+'\')).MakeRelativeUri([Uri]($taskBuild.TrimEnd('\')+'\')).ToString()).TrimEnd('/')
 foreach($taskProbe in @('native','job')){Invoke-PhaseTwoTool ($taskProbe+'-paths') 'powershell.exe' ('-NoProfile -ExecutionPolicy Bypass -File "'+(Join-Path $PSScriptRoot ('test-speech-worker-'+$taskProbe+'.ps1'))+'" -OutputDirectory "'+$taskRelativeBuild+'"')}
 Invoke-PhaseTwoQa 'native' '--speech-worker-test --mode native' 90
}
if($Mode -in @('Full','Quality')){
 $taskCases=Join-Path $PSScriptRoot 'speech-quality-cases.json'
 Invoke-PhaseTwoQa 'candidate' ('--speech-quality-test --pipeline phase2 --cases "'+$taskCases+'"') 900
 Invoke-PhaseTwoQa 'retry-candidate' ('--speech-quality-test --pipeline phase2 --confidence-retry --cases "'+$taskCases+'"') 900
 & (Join-Path $PSScriptRoot 'compare-phase2-speech.ps1') -Baseline (Join-Path $taskRoot 'work/phase1-quality/candidate.json') -Candidate (Join-Path $taskReports 'candidate.json') -Output (Join-Path $taskReports 'quality.json') -Executable $taskExe
 & (Join-Path $PSScriptRoot 'compare-phase2-speech.ps1') -Baseline (Join-Path $taskReports 'candidate.json') -Candidate (Join-Path $taskReports 'retry-candidate.json') -Output (Join-Path $taskReports 'retry-quality.json') -Executable $taskExe
}
foreach($taskMode in @('latency','cycles','soak')){if($Mode -eq 'Full' -or $Mode.ToLowerInvariant() -eq $taskMode){Invoke-PhaseTwoQa $taskMode ('--speech-worker-test --mode '+$taskMode) $(if($taskMode -eq 'soak'){2100}else{1200})}}
$taskGates=[ordered]@{native='Unverified';quality='Unverified';confidenceRetry='Unverified';captureLatency='Unverified';finalLatency='Unverified';firstDraft='Unverified';cycles='Unverified';resources='Unverified';hardwareSwitch='Unverified';privateVideo='Unverified'}
foreach($taskItem in @(@{File='native';Gate='native'},@{File='quality';Gate='quality'},@{File='retry-quality';Gate='confidenceRetry'},@{File='latency';Gate='captureLatency'},@{File='cycles';Gate='cycles'},@{File='soak';Gate='resources'})){
 $taskPath=Join-Path $taskReports ($taskItem.File+'.json')
 if(Test-Path -LiteralPath $taskPath){$taskRecord=Get-Content -LiteralPath $taskPath -Raw -Encoding UTF8 | ConvertFrom-Json;if($taskRecord.complete -or $taskItem.File -in @('quality','retry-quality')){$taskGates[$taskItem.Gate]=$taskRecord.status;if($taskItem.File -eq 'quality'){$taskGates.finalLatency=$taskRecord.finalLatency}}}
}
$taskAllowed=@($taskGates.Values | Where-Object {$_ -ne 'Pass'}).Count -eq 0
[IO.File]::WriteAllText((Join-Path $taskReports 'gates.json'),(@{productionAllowed=$taskAllowed;gates=$taskGates;reason='Physical switching and private-video reference require independent local human evidence; missing/failed gates never authorize installation.'}|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
Write-Output "Acceptance gates recorded; production allowed: $taskAllowed"
