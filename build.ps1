param([switch]$Publish,[string]$OutputDirectory='outputs/AudioTranscriber')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $root 'work/cli'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
$env:NUGET_PACKAGES=Join-Path $root 'work/packages'
$dotnet=Join-Path $root 'work/dotnet/dotnet.exe'
if(!(Test-Path $dotnet)) { $dotnet='dotnet' }
if($Publish) {
    & $dotnet publish src/AudioTranscriber -c Release -r win-x64 --self-contained true -o $OutputDirectory -m:1 -nodeReuse:false
    if($LASTEXITCODE -ne 0){throw 'Application publish failed'}
    & $dotnet publish src/WhisperWorker -c Release -r win-x64 --self-contained true -o (Join-Path $OutputDirectory 'worker') -m:1 -nodeReuse:false
    if($LASTEXITCODE -ne 0){throw 'Worker publish failed'}
} else {
    & $dotnet build AudioTranscriber.sln -c Release -m:1 -nodeReuse:false
    if($LASTEXITCODE -ne 0){throw 'Build failed'}
}
