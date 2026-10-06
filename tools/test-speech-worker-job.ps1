param([string]$OutputDirectory='dist-phase2',[string]$FixturePath='work/phase1-quality/fixtures/zh-normal.wav')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskOutput=[IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
$taskAssembly=[Reflection.Assembly]::LoadFrom((Join-Path $taskOutput 'Yike.exe'))
$taskAttach=$taskAssembly.GetType('WindowsTranslator.ProcessJob',$true).GetMethod('AttachOrTerminate',[Reflection.BindingFlags]'Public,Static')
Add-Type -TypeDefinition @'
using System;using System.IO;using System.Threading.Tasks;
public static class YikeJobProbeDrain {
 public static async Task Run(Stream stream){var bytes=new byte[4096];try{while(await stream.ReadAsync(bytes,0,bytes.Length).ConfigureAwait(false)>0)Array.Clear(bytes,0,bytes.Length);}catch(IOException){}catch(ObjectDisposedException){}}
}
'@
$taskAudio=Join-Path ([IO.Path]::GetTempPath()) ('Yike-voice-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskAudio | Out-Null
try{
 Copy-Item -LiteralPath (Join-Path $taskRoot $FixturePath) -Destination (Join-Path $taskAudio 'sample.wav')
 foreach($taskCase in @('eof','forced-native-exit')){
  $taskInstance=[Guid]::NewGuid().ToString();$taskSession=[Guid]::NewGuid().ToString();$taskControl='00000000-0000-0000-0000-000000000001'
  $taskInfo=New-Object Diagnostics.ProcessStartInfo
  $taskInfo.FileName=Join-Path $taskOutput 'YikeSpeechWorker.exe';$taskInfo.Arguments="--instance-id $taskInstance --parent-pid $PID --file-test-root `"$taskAudio`""
  $taskInfo.UseShellExecute=$false;$taskInfo.CreateNoWindow=$true;$taskInfo.RedirectStandardInput=$true;$taskInfo.RedirectStandardOutput=$true;$taskInfo.RedirectStandardError=$true
  $taskProcess=New-Object Diagnostics.Process;$taskProcess.StartInfo=$taskInfo;$taskJob=$null
  try{
   if(!$taskProcess.Start()){throw 'Owned job probe could not start'}
   $taskJob=$taskAttach.Invoke($null,[object[]]@($taskProcess.PSObject.BaseObject))
   $taskDrain=[YikeJobProbeDrain]::Run($taskProcess.StandardError.BaseStream)
   function Send-JobProbe([string]$Kind,[string]$Session,$Payload){
    $taskRequest=[Guid]::NewGuid().ToString();$taskProcess.StandardInput.WriteLine((@{Version=1;InstanceId=$taskInstance;SessionId=$Session;RequestId=$taskRequest;Kind=$Kind;Payload=$Payload}|ConvertTo-Json -Compress -Depth 4));$taskProcess.StandardInput.Flush();return $taskRequest
   }
   function Read-JobProbe([string]$Request){
    $taskRead=$taskProcess.StandardOutput.ReadLineAsync();if(!$taskRead.Wait(20000)-or !$taskRead.Result){throw 'Owned job probe response missing'}
    $taskReply=$taskRead.Result|ConvertFrom-Json;if($taskReply.RequestId-ne $Request-or $taskReply.InstanceId-ne $taskInstance){throw 'Owned job probe response mismatch'};return $taskReply
   }
   $taskRequest=Send-JobProbe 'hello' $taskControl @{};$taskReply=Read-JobProbe $taskRequest
   if($taskReply.Kind-ne 'ready'-or $taskReply.Payload.contextInitializationCount-ne 0){throw 'Job handshake unexpectedly loaded a model'}
   if($taskCase-eq 'eof'){
    $taskClock=[Diagnostics.Stopwatch]::StartNew();$taskProcess.StandardInput.Close()
    if(!$taskProcess.WaitForExit(3000)-or $taskProcess.ExitCode-ne 0){throw 'Pipe EOF did not release owned worker'}
   }else{
    $taskRequest=Send-JobProbe 'preheat' $taskControl @{};$taskReply=Read-JobProbe $taskRequest
    if($taskReply.Kind-ne 'ready'-or $taskReply.Payload.contextInitializationCount-ne 1){throw 'Native job probe not ready'}
    $taskRequest=Send-JobProbe 'decode' $taskSession @{language='zh';audioPath=(Join-Path $taskAudio 'sample.wav');useVad=$false;timeoutMilliseconds=30000}
    Start-Sleep -Milliseconds 100
    $taskClock=[Diagnostics.Stopwatch]::StartNew();$taskJob.Dispose();$taskJob=$null
    if(!$taskProcess.WaitForExit(3000)){throw 'Job close failed to end native execution within three seconds'}
   }
   Write-Output "PASS owned job $taskCase; exit=$($taskProcess.ExitCode); elapsed=$($taskClock.ElapsedMilliseconds)ms; no microphone"
  }finally{
   if($taskJob){$taskJob.Dispose()}
   if(!$taskProcess.HasExited){$taskProcess.Kill();$taskProcess.WaitForExit(3000)|Out-Null}
   $taskProcess.Dispose()
  }
 }
}finally{
 $taskFull=[IO.Path]::GetFullPath($taskAudio);$taskTemp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
 if(!$taskFull.StartsWith($taskTemp,[StringComparison]::OrdinalIgnoreCase)-or (Split-Path -Leaf $taskFull)-notmatch '^Yike-voice-[0-9a-f]{32}$'){throw 'Refusing cleanup of unowned probe directory'}
 if(Test-Path -LiteralPath $taskFull){if((Get-Item -LiteralPath $taskFull).Attributes-band [IO.FileAttributes]::ReparsePoint){throw 'Refusing reparse cleanup'};Remove-Item -LiteralPath $taskFull -Recurse -Force}
}
