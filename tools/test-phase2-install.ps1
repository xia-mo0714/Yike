param([Parameter(Mandatory=$true)][string]$InstallerPath,
      [Parameter(Mandatory=$true)][string]$ModelSource,
      [Parameter(Mandatory=$true)][string]$ReportDirectory,
      [switch]$ExpectRejected)
$ErrorActionPreference='Stop'
$taskInstaller=(Resolve-Path -LiteralPath $InstallerPath).Path
$taskModel=(Resolve-Path -LiteralPath $ModelSource).Path
$taskReports=[IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $taskReports -Force | Out-Null
$taskParent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$taskRoot=Join-Path $taskParent ('Yike-install-test-'+[guid]::NewGuid().ToString('N'))
function Snapshot-ProtectedState {
    $taskState=[ordered]@{registry=$null;files=@();directories=@()}
    $taskKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Yike'
    $taskRegistered=''
    if(Test-Path -LiteralPath $taskKey){
        $taskProperties=Get-ItemProperty -LiteralPath $taskKey
        $taskRegistered=$taskProperties.InstallLocation
        $taskState.registry=@($taskProperties.PSObject.Properties | Where-Object {$_.Name -notlike 'PS*'} | Sort-Object Name | ForEach-Object {[ordered]@{name=$_.Name;value=$_.Value}})
    }
    $taskFolders=@((Join-Path $env:LOCALAPPDATA 'Yike'),(Join-Path $env:APPDATA 'Yike'))
    if($taskRegistered){$taskFolders+=$taskRegistered}
    $taskFiles=@((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Yike.lnk'),(Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Yike\Yike.lnk'))
    foreach($taskFolder in $taskFolders){
        $taskState.directories+=@([ordered]@{path=$taskFolder;exists=(Test-Path -LiteralPath $taskFolder)})
        if(Test-Path -LiteralPath $taskFolder -PathType Container){
            $taskItems=@(Get-ChildItem -LiteralPath $taskFolder -Recurse -Force)
            if(@($taskItems | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Protected tree has a link; refuse an incomplete snapshot'}
            $taskFiles+=@($taskItems | Where-Object {!$_.PSIsContainer} | ForEach-Object FullName)
            $taskState.directories+=@($taskItems | Where-Object PSIsContainer | Sort-Object FullName | ForEach-Object {[ordered]@{path=$_.FullName;exists=$true}})
        }
    }
    $taskState.files=@($taskFiles | Sort-Object -Unique | ForEach-Object {[ordered]@{path=$_;hash=if(Test-Path -LiteralPath $_ -PathType Leaf){(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}else{'missing'}}})
    return ($taskState | ConvertTo-Json -Depth 10 -Compress)
}
function Run-OwnedChild([string]$file,[string]$arguments,[int]$seconds){
    $taskProcess=Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $null=$taskProcess.Handle
    try {
        if(!$taskProcess.WaitForExit($seconds*1000)){
            $taskProcess.Kill();$taskProcess.WaitForExit()
            throw 'Owned isolated child timed out'
        }
        $taskProcess.WaitForExit()
        return $taskProcess.ExitCode
    } finally {$taskProcess.Dispose()}
}
function Assert-OwnedRoot([string]$path){
    $taskFull=[IO.Path]::GetFullPath($path).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($taskFull) -ne $taskParent -or [IO.Path]::GetFileName($taskFull) -notmatch '^Yike-install-test-[0-9a-f]{32}(\.uninstall-[0-9a-f]{32})?$'){throw 'Unsafe diagnostic cleanup target'}
    return $taskFull
}
$null=Assert-OwnedRoot $taskRoot
if(Test-Path -LiteralPath $taskRoot){throw 'Diagnostic root already exists'}
$taskBefore=Snapshot-ProtectedState
$taskStatus='Fail'
$taskError=''
$taskRemoved=$false
$taskSelfTest=$false
$taskRejected=$false
try {
    if([Diagnostics.FileVersionInfo]::GetVersionInfo($taskInstaller).FileVersion -ne '2.3.0.0'){throw 'Candidate version must remain 2.3.0.0'}
    if((Get-Item -LiteralPath $taskInstaller).Length -gt 21747712){throw 'Candidate exceeds phase-two installer size limit'}
    Add-Type -AssemblyName System.IO.Compression
    $taskAssembly=[Reflection.Assembly]::LoadFile($taskInstaller)
    $taskPayload=$taskAssembly.GetManifestResourceStream('payload.zip')
    if(!$taskPayload){throw 'Embedded payload missing'}
    try {
        $taskZip=New-Object IO.Compression.ZipArchive($taskPayload,[IO.Compression.ZipArchiveMode]::Read)
        try {
            $taskEntries=@($taskZip.Entries | ForEach-Object {$_.FullName.Replace('/','\')})
            if(@($taskEntries | Where-Object {$_ -match '(?i)(ggml-small-q8_0\.bin|ggml-base-q5_1\.bin)$'}).Count){throw 'Lightweight candidate bundled a main model'}
        } finally {$taskZip.Dispose()}
    } finally {$taskPayload.Dispose()}
    $taskCode=Run-OwnedChild $taskInstaller ('--verify-install --install-dir "'+$taskRoot+'" --model-source "'+$taskModel+'"') 90
    if($ExpectRejected){
        if($taskCode -eq 0){throw 'Invalid payload was accepted'}
        if(Test-Path -LiteralPath $taskRoot){throw 'Rejected payload left an owned staging directory'}
        $taskRejected=$true;$taskRemoved=$true
    } else {
        if($taskCode -ne 0){throw 'Restricted diagnostic installation failed'}
        if(!(Test-Path -LiteralPath (Join-Path $taskRoot 'YikeSpeechWorker.exe')) -or !(Test-Path -LiteralPath (Join-Path $taskRoot 'whisper-1.9.4-abi.json'))){throw 'Worker or ABI metadata missing'}
        if((Get-FileHash -LiteralPath (Join-Path $taskRoot 'whisper-runtime\ggml-small-q8_0.bin')).Hash -ne (Get-FileHash -LiteralPath $taskModel).Hash){throw 'Verified model was not reused exactly'}
        $taskCode=Run-OwnedChild (Join-Path $taskRoot 'Yike.exe') '--self-test' 90
        if($taskCode -ne 0 -or !(Get-Content -LiteralPath (Join-Path $taskRoot 'test-results.txt') -Raw).Contains('ALL TESTS PASSED')){throw 'Installed self-test failed'}
        $taskSelfTest=$true
        $taskUninstaller=Join-Path $taskRoot 'uninstall.ps1'
        $taskCode=Run-OwnedChild 'powershell.exe' ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$taskUninstaller+'"') 30
        if($taskCode -ne 0){throw 'Isolated uninstaller failed'}
        $taskDeadline=[DateTime]::UtcNow.AddSeconds(20)
        do {
            $taskRemaining=@(Get-ChildItem -LiteralPath $taskParent -Directory | Where-Object {$_.FullName -eq $taskRoot -or $_.Name -like ([IO.Path]::GetFileName($taskRoot)+'.uninstall-*')})
            if($taskRemaining.Count){Start-Sleep -Milliseconds 200}
        } while($taskRemaining.Count -and [DateTime]::UtcNow -lt $taskDeadline)
        if($taskRemaining.Count){throw 'Owned install/quarantine remained after uninstall'}
        $taskOwnedHelpers=@(Get-Process -Name Yike,YikeSpeechWorker,whisper-cli,whisper-stream -ErrorAction SilentlyContinue | Where-Object {try{$_.Path.StartsWith($taskRoot+'\',[StringComparison]::OrdinalIgnoreCase)}catch{$false}})
        if($taskOwnedHelpers.Count){throw 'Owned isolated helper remained'}
        $taskRemoved=$true
    }
    $taskStatus='Pass'
} catch {$taskError=$_.Exception.Message}
finally {
    $taskUnchanged=(Snapshot-ProtectedState) -eq $taskBefore
    if(!$taskUnchanged){$taskStatus='Fail';$taskError='Protected installed state changed'}
    $taskReport=[ordered]@{complete=$true;status=$taskStatus;failure=$taskError;expectedRejection=[bool]$ExpectRejected;rejected=$taskRejected;isolatedSelfTest=$taskSelfTest;ownedResourcesRemoved=$taskRemoved;protectedStateUnchanged=$taskUnchanged;protectedLocalAppData=$true;installerBytes=(Get-Item -LiteralPath $taskInstaller).Length;sha256=(Get-FileHash -LiteralPath $taskInstaller).Hash;version=[Diagnostics.FileVersionInfo]::GetVersionInfo($taskInstaller).FileVersion}
    $taskName=if($ExpectRejected){'rejected-install.json'}else{'install.json'}
    [IO.File]::WriteAllText((Join-Path $taskReports $taskName),($taskReport | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
}
if($taskStatus -ne 'Pass'){throw $taskError}
Write-Output 'PASS: isolated payload result, protected LOCALAPPDATA/registration/installed files unchanged.'
