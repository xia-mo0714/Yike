param([string]$OutputDirectory='.\dist-phase2',[string]$FixturePath='.\work\phase1-quality\fixtures\zh-normal.wav')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOutput=[IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
$taskFixture=[IO.Path]::GetFullPath((Join-Path $taskRoot $FixturePath))
$taskModel=Join-Path $taskOutput 'whisper-runtime/ggml-small-q8_0.bin'
if(!(Test-Path -LiteralPath $taskFixture)){throw 'Frozen native fixture unavailable'}
if((Get-FileHash -LiteralPath $taskModel).Hash -ne '49c8fb02b65e6049d5fa6c04f81f53b867b5ec9540406812c643f177317f779f'){throw 'Pinned model mismatch'}
Add-Type -TypeDefinition @'
using System; using System.IO; using System.Threading; using System.Threading.Tasks;
public static class YikeNativeProbeDrain {
 public static async Task Run(Stream stream,CancellationToken token){var bytes=new byte[4096];try{while(await stream.ReadAsync(bytes,0,bytes.Length,token).ConfigureAwait(false)>0)Array.Clear(bytes,0,bytes.Length);}catch(IOException){}catch(ObjectDisposedException){}catch(OperationCanceledException){}}
}
'@
function Read-ProbeMessage($Process){
 $taskRead=$Process.StandardOutput.ReadLineAsync()
 if(!$taskRead.Wait(20000)){throw 'Native response timed out'}
 if(!$taskRead.Result){throw 'Native protocol EOF'}
 if([Text.Encoding]::UTF8.GetByteCount($taskRead.Result) -gt 1048576){throw 'Native response oversized'}
 return ($taskRead.Result | ConvertFrom-Json)
}
function Write-ProbeCommand($Process,[string]$Kind,[string]$Instance,[string]$Session,$Payload){
 $taskRequest=[Guid]::NewGuid().ToString()
 $taskMessage=@{Version=1;InstanceId=$Instance;SessionId=$Session;RequestId=$taskRequest;Kind=$Kind;Payload=$Payload} | ConvertTo-Json -Compress -Depth 4
 $Process.StandardInput.WriteLine($taskMessage);$Process.StandardInput.Flush()
 return $taskRequest
}
function Require-ProbeReply($Process,[string]$Kind,[string]$Instance,[string]$Session,[string]$Request){
 $taskReply=Read-ProbeMessage $Process
 if($taskReply.Kind -ne $Kind -or $taskReply.InstanceId -ne $Instance -or $taskReply.SessionId -ne $Session -or $taskReply.RequestId -ne $Request){throw "Native reply mismatch: $($taskReply.Kind), $($taskReply.Payload.failureCode)"}
 return $taskReply
}
function Remove-OwnedProbeDirectory([string]$Path,[string]$Parent){
 $taskFull=[IO.Path]::GetFullPath($Path);$taskParent=[IO.Path]::GetFullPath($Parent).TrimEnd('\')+'\'
 if(!$taskFull.StartsWith($taskParent,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing cleanup outside probe parent'}
 if(Test-Path -LiteralPath $taskFull){
  if((Get-Item -LiteralPath $taskFull).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Refusing probe reparse cleanup'}
  if(@(Get-ChildItem -LiteralPath $taskFull -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -gt 0){throw 'Refusing probe child reparse cleanup'}
  Remove-Item -LiteralPath $taskFull -Recurse -Force
 }
}
$taskScratchParent=Join-Path $taskRoot 'work/phase2-native-paths'
$taskScratch=Join-Path $taskScratchParent ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskScratch -Force | Out-Null
$taskControl='00000000-0000-0000-0000-000000000001'
$taskRecords=@()
try{
 $taskChineseCase=([string][char]0x4e2d)+[char]0x6587+' Yike'
 foreach($taskCase in @('ascii','Yike native',$taskChineseCase)){
  $taskCaseRoot=Join-Path $taskScratch $taskCase
  New-Item -ItemType Directory -Path (Join-Path $taskCaseRoot 'whisper-runtime/Release') -Force | Out-Null
  Copy-Item -LiteralPath (Join-Path $taskOutput 'YikeSpeechWorker.exe'),(Join-Path $taskOutput 'whisper-1.9.4-abi.json') -Destination $taskCaseRoot
  # This unverified transitive dependency must not be searched beside the worker.
  [IO.File]::WriteAllBytes((Join-Path $taskCaseRoot 'VCOMP140.DLL'),[byte[]]@(0,1,2,3))
  Copy-Item (Join-Path $taskOutput 'whisper-runtime/Release/*.dll') -Destination (Join-Path $taskCaseRoot 'whisper-runtime/Release')
  Copy-Item -LiteralPath (Join-Path $taskOutput 'whisper-runtime/ggml-silero-v6.2.0.bin') -Destination (Join-Path $taskCaseRoot 'whisper-runtime')
  New-Item -ItemType HardLink -Path (Join-Path $taskCaseRoot 'whisper-runtime/ggml-small-q8_0.bin') -Target $taskModel | Out-Null
  $taskPolluted=Join-Path $taskCaseRoot 'polluted-current-directory';New-Item -ItemType Directory -Path $taskPolluted | Out-Null
  foreach($taskDll in @('whisper.dll','ggml.dll','ggml-base.dll','SDL2.dll')){[IO.File]::WriteAllBytes((Join-Path $taskPolluted $taskDll),[byte[]]@(0,1,2,3))}
  $taskAudioRoot=Join-Path ([IO.Path]::GetTempPath()) ('Yike-voice-'+[Guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $taskAudioRoot | Out-Null
  Copy-Item -LiteralPath $taskFixture -Destination (Join-Path $taskAudioRoot 'sample.wav')
  $taskInstance=[Guid]::NewGuid().ToString();$taskSession=[Guid]::NewGuid().ToString()
  $taskInfo=New-Object Diagnostics.ProcessStartInfo
  $taskInfo.FileName=Join-Path $taskCaseRoot 'YikeSpeechWorker.exe';$taskInfo.Arguments="--instance-id $taskInstance --parent-pid $PID --file-test-root `"$taskAudioRoot`""
  $taskInfo.WorkingDirectory=$taskPolluted;$taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true
  $taskInfo.RedirectStandardInput=$true;$taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
  $taskInfo.StandardOutputEncoding=New-Object Text.UTF8Encoding($false);$taskInfo.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
  $taskProcess=New-Object Diagnostics.Process;$taskProcess.StartInfo=$taskInfo;$taskDrainStop=New-Object Threading.CancellationTokenSource
  try{
   if(!$taskProcess.Start()){throw 'Native worker start failed'}
   $taskDrain=[YikeNativeProbeDrain]::Run($taskProcess.StandardError.BaseStream,$taskDrainStop.Token)
   $taskRequest=Write-ProbeCommand $taskProcess 'hello' $taskInstance $taskControl @{}
   $taskHello=Require-ProbeReply $taskProcess 'ready' $taskInstance $taskControl $taskRequest
   if($taskHello.Payload.contextInitializationCount -ne 0){throw 'Handshake loaded model'}
   $taskRequest=Write-ProbeCommand $taskProcess 'preheat' $taskInstance $taskControl @{}
   $taskReady=Require-ProbeReply $taskProcess 'ready' $taskInstance $taskControl $taskRequest
   if($taskReady.Payload.contextInitializationCount -ne 1 -or $taskReady.Payload.isWarm){throw 'Preheat did not initialize exactly once'}
   $taskValid=0
   foreach($taskDecodeIndex in 1,2){
    $taskRequest=Write-ProbeCommand $taskProcess 'decode' $taskInstance $taskSession @{audioPath=(Join-Path $taskAudioRoot 'sample.wav');language='zh';useVad=$true;timeoutMilliseconds=30000}
    $taskFinal=Require-ProbeReply $taskProcess 'final' $taskInstance $taskSession $taskRequest
    if([string]::IsNullOrWhiteSpace($taskFinal.Payload.text) -or $taskFinal.Payload.contextInitializationCount -ne 1 -or !$taskFinal.Payload.isWarm -or @($taskFinal.Payload.probabilities).Count -eq 0){throw 'Decode did not reuse model or return actual tokens'}
    $taskValid++
   }
   $taskVad=Join-Path $taskCaseRoot 'whisper-runtime/ggml-silero-v6.2.0.bin';$taskVadBackup=$taskVad+'.owned-backup';Move-Item -LiteralPath $taskVad -Destination $taskVadBackup
   foreach($taskVadState in @('missing','corrupt')){
    if($taskVadState -eq 'corrupt'){[IO.File]::WriteAllBytes($taskVad,[byte[]]@(0,1,2,3))}
    $taskRequest=Write-ProbeCommand $taskProcess 'decode' $taskInstance $taskSession @{audioPath=(Join-Path $taskAudioRoot 'sample.wav');language='zh';useVad=$true;timeoutMilliseconds=30000}
    $taskFailure=Require-ProbeReply $taskProcess 'failure' $taskInstance $taskSession $taskRequest
    if($taskFailure.Payload.failureCode -ne 'vad_model_invalid'){throw 'Invalid VAD was not rejected separately'}
   }
   Remove-Item -LiteralPath $taskVad;Move-Item -LiteralPath $taskVadBackup -Destination $taskVad
   $taskCancelSession=[Guid]::NewGuid().ToString()
   $taskDecodeRequest=Write-ProbeCommand $taskProcess 'decode' $taskInstance $taskCancelSession @{audioPath=(Join-Path $taskAudioRoot 'sample.wav');language='zh';useVad=$false;timeoutMilliseconds=30000}
   $taskCancelRequest=Write-ProbeCommand $taskProcess 'cancel' $taskInstance $taskCancelSession @{}
   $taskCancelClock=[Diagnostics.Stopwatch]::StartNew();$taskCancelled=$false
   while(!$taskCancelled){$taskReply=Read-ProbeMessage $taskProcess;if($taskReply.Kind -eq 'cancelled' -and $taskReply.RequestId -eq $taskCancelRequest){$taskCancelled=$true}else{if($taskReply.RequestId -ne $taskDecodeRequest -or $taskReply.Kind -ne 'failure' -or $taskReply.Payload.failureCode -ne 'native_cancelled'){throw 'Cancelled native attempt returned text'}}}
   if($taskCancelClock.ElapsedMilliseconds -gt 3000){throw 'Native cancel exceeded three seconds'}
   $taskRequest=Write-ProbeCommand $taskProcess 'shutdown' $taskInstance $taskControl @{}
   $taskClosed=Require-ProbeReply $taskProcess 'cancelled' $taskInstance $taskControl $taskRequest
   if(!$taskProcess.WaitForExit(3000) -or $taskProcess.ExitCode -ne 0){throw 'Owned worker did not exit cleanly'}
   if($taskCase -eq 'ascii'){
    & (Join-Path $taskOutput 'YikeSpeechWorker.Tests.exe') --native-file-contract $taskAudioRoot
    if($LASTEXITCODE -ne 0){throw 'Actual native concurrency/callback contract failed'}
   }
   $taskRecords+=@{pathCase=$taskCase;validDecodes=$taskValid;contextInitializationCount=1;cancelMilliseconds=$taskCancelClock.ElapsedMilliseconds;exitCode=$taskProcess.ExitCode;pollutedCwdRejected=$true;unverifiedApplicationDependencyIgnored=$true;missingVadRejected=$true;corruptVadRejected=$true}
   Write-Output "PASS native path $taskCase; two decodes, one context, owned worker exited"
  }finally{
   if($taskProcess.Id -and !$taskProcess.HasExited){$taskProcess.Kill();$taskProcess.WaitForExit(3000) | Out-Null}
   $taskDrainStop.Cancel();$taskProcess.Dispose();$taskDrainStop.Dispose()
   Remove-OwnedProbeDirectory $taskAudioRoot ([IO.Path]::GetTempPath())
  }
 }
 $taskRecords | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskScratchParent 'last-probe.json') -Encoding UTF8
 Write-Output 'PASS native ABI, path safety, reuse, cancellation'
}finally{Remove-OwnedProbeDirectory $taskScratch $taskScratchParent}
