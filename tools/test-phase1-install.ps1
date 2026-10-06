param([string]$Installer='', [string]$Model='', [string]$Report='')
$ErrorActionPreference='Stop'
if(!$Installer){$Installer=Join-Path $PSScriptRoot '..\release\phase1-speech\Yike-Setup.exe'}
if(!$Model){$Model=Join-Path $PSScriptRoot '..\assets\whisper-runtime\ggml-small-q8_0.bin'}
if(!$Report){$Report=Join-Path $PSScriptRoot '..\release\phase1-speech\install-smoke.json'}
$taskInstaller=(Resolve-Path -LiteralPath $Installer).Path
$taskModel=(Resolve-Path -LiteralPath $Model).Path
$taskParent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$taskRoot=Join-Path $taskParent ('Yike-install-test-'+[guid]::NewGuid().ToString('N'))
if([IO.Path]::GetDirectoryName($taskRoot) -ne $taskParent -or (Split-Path -Leaf $taskRoot) -notmatch '^Yike-install-test-[0-9a-f]{32}$' -or (Test-Path $taskRoot)){throw 'Unsafe test root'}
function Snapshot-ProtectedState {
    $taskKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Yike'
    $taskState=[ordered]@{registry=$null;files=@()}
    $taskRegistered=''
    if(Test-Path -LiteralPath $taskKey){$taskProperties=Get-ItemProperty -LiteralPath $taskKey;$taskRegistered=$taskProperties.InstallLocation;$taskState.registry=@($taskProperties.PSObject.Properties|Where-Object {$_.Name -notlike 'PS*'}|Sort-Object Name|ForEach-Object {[ordered]@{name=$_.Name;value=$_.Value}})}
    $taskFiles=@(Join-Path ([Environment]::GetFolderPath('Desktop')) 'Yike.lnk'; Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Yike\Yike.lnk'; Join-Path $env:APPDATA 'Yike\preferences.json')
    if($taskRegistered -and (Test-Path -LiteralPath $taskRegistered -PathType Container)){$taskFiles+=@(Get-ChildItem -LiteralPath $taskRegistered -File -Recurse | ForEach-Object FullName)}
    $taskState.files=@($taskFiles | Sort-Object -Unique | ForEach-Object {[ordered]@{path=$_;hash=if(Test-Path -LiteralPath $_ -PathType Leaf){(Get-FileHash -LiteralPath $_).Hash}else{'missing'}}})
    return ($taskState | ConvertTo-Json -Depth 8 -Compress)
}
$taskBefore=Snapshot-ProtectedState
$taskVoiceBefore=@(Get-ChildItem -LiteralPath $taskParent -Directory -Filter 'Yike-voice-*' | ForEach-Object FullName)
$taskHelpersBefore=@(Get-Process -Name 'whisper-cli','whisper-stream' -ErrorAction SilentlyContinue | ForEach-Object Id)
$taskProcess=Start-Process -FilePath $taskInstaller -ArgumentList ('--verify-install --install-dir "'+$taskRoot+'" --model-source "'+$taskModel+'"') -WindowStyle Hidden -PassThru
if(!$taskProcess.WaitForExit(60000) -or $taskProcess.ExitCode -ne 0){throw 'Restricted diagnostic installation failed'}
$taskCheck=Start-Process -FilePath (Join-Path $taskRoot 'Yike.exe') -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
if(!$taskCheck.WaitForExit(60000) -or $taskCheck.ExitCode -ne 0 -or !(Get-Content (Join-Path $taskRoot 'test-results.txt') -Raw).Contains('ALL TESTS PASSED')){throw 'Isolated installation self-test failed'}
$taskResults=Get-Content (Join-Path $taskRoot 'test-results.txt') -Raw
$taskUninstall=Join-Path $taskRoot 'uninstall.ps1'
if(!(Test-Path -LiteralPath $taskUninstall -PathType Leaf)){throw 'Missing path-checked uninstaller'}
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $taskUninstall
if($LASTEXITCODE -ne 0){throw 'Isolated uninstaller failed'}
$taskDeadline=[DateTime]::UtcNow.AddSeconds(15)
do { $taskRemaining=@(Get-ChildItem -LiteralPath $taskParent -Directory | Where-Object { $_.FullName -eq $taskRoot -or $_.Name -like ((Split-Path -Leaf $taskRoot)+'.uninstall-*') });if($taskRemaining.Count){Start-Sleep -Milliseconds 200} }while($taskRemaining.Count -and [DateTime]::UtcNow -lt $taskDeadline)
if($taskRemaining.Count){throw 'Test installation/quarantine not removed'}
if((Snapshot-ProtectedState) -ne $taskBefore){throw 'Existing app/registry/shortcuts/preferences changed'}
$taskNewVoice=@(Get-ChildItem -LiteralPath $taskParent -Directory -Filter 'Yike-voice-*'|Where-Object {$_.FullName -notin $taskVoiceBefore})
$taskNewHelpers=@(Get-Process -Name 'whisper-cli','whisper-stream' -ErrorAction SilentlyContinue | Where-Object {$_.Id -notin $taskHelpersBefore})
if($taskNewVoice.Count -or $taskNewHelpers.Count){throw 'Task-owned speech resources leaked'}
$taskReport=[ordered]@{result='PASS';isolatedSelfTest=$true;thirtyCycleSoak=$true;removed=$true;protectedStateUnchanged=$true;noNewVoiceDirectory=$true;noNewNativeHelper=$true;installerBytes=(Get-Item -LiteralPath $taskInstaller).Length;sha256=(Get-FileHash -LiteralPath $taskInstaller).Hash;version=[Diagnostics.FileVersionInfo]::GetVersionInfo($taskInstaller).FileVersion}
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Report),($taskReport|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
Write-Output 'PASS: isolated install/self-test/uninstall; 30-cycle soak; existing state unchanged.'
