param([string]$OutputDirectory='dist',[switch]$SkipRuntimes,[switch]$Test)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOutput=if([IO.Path]::IsPathRooted($OutputDirectory)){[IO.Path]::GetFullPath($OutputDirectory)}else{[IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))}
New-Item -ItemType Directory -Force -Path $taskOutput | Out-Null
$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$taskShared=@('SpeechWorkerProtocol','SpeechRecognitionCandidate','SpeechRuntimePaths','WhisperVadConfiguration','SpeechCaptureEndpoint','SpeechText.Core','SpeechDraft','SpeechLanguage') | ForEach-Object {Join-Path $taskRoot "src/Infrastructure/Speech/$_.cs"}
$taskWorker=@(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'speech-worker') -Filter '*.cs' | ForEach-Object {$_.FullName})
$taskRefs=@('/r:System.dll','/r:System.Core.dll','/r:System.Web.Extensions.dll')
$taskManifest='/win32manifest:'+(Join-Path $taskRoot 'speech-worker/app.manifest')
if($Test){
 $taskTests=@(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'speech-worker/tests') -Filter '*.cs' | ForEach-Object {$_.FullName})
 & $taskCompiler /nologo /nowarn:0649 /optimize+ /platform:x64 /target:exe /main:WindowsTranslator.SpeechWorkerTests $taskManifest "/out:$taskOutput/YikeSpeechWorker.Tests.exe" @taskRefs @taskShared @taskWorker @taskTests
}else{
 & $taskCompiler /nologo /nowarn:0649 /optimize+ /platform:x64 /target:exe /main:WindowsTranslator.SpeechWorkerProgram $taskManifest "/out:$taskOutput/YikeSpeechWorker.exe" @taskRefs @taskShared @taskWorker
}
if($LASTEXITCODE -ne 0){throw 'Speech worker compilation failed'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'whisper-1.9.4-abi.json') -Destination $taskOutput -Force
if(!$SkipRuntimes -and !$Test){
 $taskRuntimeSource=Join-Path $taskRoot 'assets/whisper-runtime'
 $taskRuntimeTarget=Join-Path $taskOutput 'whisper-runtime'
 if([IO.Path]::GetFullPath($taskRuntimeSource) -eq [IO.Path]::GetFullPath($taskRuntimeTarget)){throw 'Runtime source cannot be build output'}
 New-Item -ItemType Directory -Force -Path (Join-Path $taskRuntimeTarget 'Release') | Out-Null
 Copy-Item (Join-Path $taskRuntimeSource 'Release/*.dll') -Destination (Join-Path $taskRuntimeTarget 'Release') -Force
 Copy-Item (Join-Path $taskRuntimeSource 'LICENSE-*.txt'),(Join-Path $taskRuntimeSource 'THIRD-PARTY-NOTICES.txt') -Destination $taskRuntimeTarget -Force
 foreach($taskModelName in @('ggml-small-q8_0.bin','ggml-silero-v6.2.0.bin')){
  $taskModelTarget=Join-Path $taskRuntimeTarget $taskModelName
  if(!(Test-Path -LiteralPath $taskModelTarget)){Copy-Item -LiteralPath (Join-Path $taskRuntimeSource $taskModelName) -Destination $taskModelTarget}
 }
}
Write-Output "Speech worker compiled: $taskOutput"
