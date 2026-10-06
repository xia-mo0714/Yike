param([string]$Destination='', [string]$LocalSource='')
$ErrorActionPreference='Stop'
$taskManifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'whisper-vad-model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if(!$Destination){$Destination=Join-Path $PSScriptRoot ('..\assets\whisper-runtime\'+$taskManifest.file)}
$taskTarget=[IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskTarget) | Out-Null
$taskTemp=$taskTarget+'.download-'+[guid]::NewGuid().ToString('N')
try{
    if($LocalSource){Copy-Item -LiteralPath $LocalSource -Destination $taskTemp}
    else {if(Get-Command curl.exe -ErrorAction SilentlyContinue){& curl.exe --fail --location --retry 2 --silent --show-error --output $taskTemp $taskManifest.url;if($LASTEXITCODE -ne 0){throw 'VAD download failed.'}}else{[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;Invoke-WebRequest -Uri $taskManifest.url -OutFile $taskTemp -UseBasicParsing}}
    if((Get-FileHash -LiteralPath $taskTemp -Algorithm SHA256).Hash -ne $taskManifest.sha256){throw 'Silero VAD SHA-256 mismatch; existing model preserved.'}
    Move-Item -LiteralPath $taskTemp -Destination $taskTarget -Force
    Get-Item -LiteralPath $taskTarget | Select-Object FullName,Length
}finally{if(Test-Path -LiteralPath $taskTemp -PathType Leaf){Remove-Item -LiteralPath $taskTemp -Force}}
