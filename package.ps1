param([switch]$Zip,[string]$Name='AudioTranscriber')
$ErrorActionPreference='Stop'
if($Name -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]*$'){throw 'Invalid package name'}
$out=Join-Path $PSScriptRoot "outputs/$Name"
& "$PSScriptRoot/build.ps1" -Publish -OutputDirectory $out
$out=[IO.Path]::GetFullPath($out)
function Remove-PackageDirectory([string]$path) {
    $resolved=[IO.Path]::GetFullPath($path)
    if(!$resolved.StartsWith($out+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing to remove outside package'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
$runtime=Join-Path $out 'worker/runtimes'
foreach($folder in Get-ChildItem -LiteralPath $runtime -Directory) {
    if($folder.Name -notin @('win-x64','cuda12','vulkan')){Remove-PackageDirectory $folder.FullName}
    elseif($folder.Name -in @('cuda12','vulkan')) {
        foreach($child in Get-ChildItem -LiteralPath $folder.FullName -Directory){if($child.Name -ne 'win-x64'){Remove-PackageDirectory $child.FullName}}
    }
}
foreach($folder in Get-ChildItem -LiteralPath $out -Directory){if($folder.Name -like 'self-test-*' -or $folder.Name -in @('diagnostic-data','crash-test-data')){Remove-PackageDirectory $folder.FullName}}
$cuda=Join-Path $PSScriptRoot 'work/cuda'
foreach($name in @('cublas64_12.dll','cublasLt64_12.dll','cudart64_12.dll')) {
    $source=Get-ChildItem -LiteralPath $cuda -Filter $name -Recurse | Select-Object -First 1
    if(!$source){throw "Missing CUDA redistribution file $name"}
    Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $out 'worker') -Force
}
foreach($name in @('vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','vcomp140.dll')) {
    Copy-Item -LiteralPath (Join-Path $env:WINDIR "System32/$name") -Destination (Join-Path $out 'worker') -Force
}
New-Item -ItemType Directory -Force (Join-Path $out 'models') | Out-Null
foreach($name in @('ggml-large-v3-turbo.bin','ggml-silero-v6.2.0.bin')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "work/models/$name") -Destination (Join-Path $out 'models') -Force
}
Copy-Item -Path "$PSScriptRoot/docs/*" -Destination $out -Recurse -Force
Get-ChildItem -LiteralPath $out -Recurse -Filter '*.pdb' | Remove-Item -Force
if($Zip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archivePath=Join-Path $PSScriptRoot "outputs/$Name-portable-win-x64.zip"
    if(Test-Path -LiteralPath $archivePath){Remove-Item -LiteralPath $archivePath -Force}
    [IO.Compression.ZipFile]::CreateFromDirectory($out,$archivePath,[IO.Compression.CompressionLevel]::Fastest,$true)
}
