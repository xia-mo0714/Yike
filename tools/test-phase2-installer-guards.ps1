param([string]$SourceDirectory='dist-phase2')
$ErrorActionPreference='Stop'
$taskProject=Split-Path -Parent $PSScriptRoot
$taskOutput=Join-Path $taskProject 'work\phase2-installer-guards'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskBadZip=Join-Path $taskOutput 'invalid-payload.zip'
[IO.File]::WriteAllBytes($taskBadZip,[byte[]](1,2,3,4))
$taskExe=Join-Path $taskOutput 'InstallerPhaseTwoTests.exe'
$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /main:InstallerPhaseTwoTests "/out:$taskExe" "/resource:$taskBadZip,payload.zip" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll (Join-Path $taskProject 'installer\Setup.cs') (Join-Path $taskProject 'installer\InstallerPhaseTwoTests.cs')
if($LASTEXITCODE -ne 0){throw 'Installer guard test compilation failed'}
$taskSource=(Resolve-Path -LiteralPath $SourceDirectory).Path
& $taskExe $taskSource (Join-Path $taskProject 'installer\uninstall.ps1')
if($LASTEXITCODE -ne 0){throw 'Installer guard tests failed'}
