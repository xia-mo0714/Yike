$ErrorActionPreference='Stop'
$taskProject=Split-Path -Parent $PSScriptRoot
$taskOutput=Join-Path $taskProject 'work\phase1-ledger\InstallerDiagnosticTests.exe'
$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $taskCompiler /nologo /target:exe /platform:x64 /main:InstallerDiagnosticTests "/out:$taskOutput" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll (Join-Path $taskProject 'installer\Setup.cs') (Join-Path $taskProject 'installer\InstallerDiagnosticTests.cs')
if($LASTEXITCODE -ne 0){throw 'Installer diagnostics compilation failed.'}
& $taskOutput
if($LASTEXITCODE -ne 0){throw 'Installer diagnostics failed.'}
