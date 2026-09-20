[CmdletBinding()]
param([string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot
$assistantRoot = [IO.Path]::GetFullPath((Join-Path $projectDirectory '../../../..'))
$project = Join-Path $projectDirectory 'Oscova.Agents.Plugins.LocalSpeech.csproj'
$destination = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $assistantRoot 'artifacts/nuget'
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null

# Keep restore and compilation settings identical for every pack of an edition.
$cpuProperties = @('-p:LocalSpeechEdition=Cpu', '-p:GeneratePackageOnBuild=false')
$cpuArguments = @($project, '-c', 'Release') + $cpuProperties
& dotnet restore $project @cpuProperties "-p:RestoreAdditionalProjectSources=$destination"
if ($LASTEXITCODE -ne 0) { throw 'CPU speech restore failed.' }
& dotnet build @cpuArguments --no-restore
if ($LASTEXITCODE -ne 0) { throw 'CPU speech build failed.' }
& dotnet pack @cpuArguments --no-build --no-restore -o $destination
if ($LASTEXITCODE -ne 0) { throw 'CPU speech packaging failed.' }

$cudaProperties = @('-p:LocalSpeechEdition=Cuda', '-p:GeneratePackageOnBuild=false',
    '--artifacts-path', (Join-Path $assistantRoot 'artifacts/local-speech/cuda'),
    '-p:IncludeProjectNameInArtifactsPaths=true', '-p:ArtifactsProjectName=Oscova.Agents.Plugins.LocalSpeech')
$cudaArguments = @($project, '-c', 'Release') + $cudaProperties
& dotnet restore $project @cudaProperties "-p:RestoreAdditionalProjectSources=$destination"
if ($LASTEXITCODE -ne 0) { throw 'CUDA speech restore failed.' }
& dotnet build @cudaArguments --no-restore
if ($LASTEXITCODE -ne 0) { throw 'CUDA speech build failed.' }

foreach ($manifest in @('Whisper.Cuda.Windows.nuspec', 'Whisper.Cuda.Linux.nuspec', 'LocalSpeech.Cuda.nuspec')) {
    & dotnet pack @cudaArguments --no-build --no-restore -o $destination "-p:NuspecFile=$(Join-Path $PSScriptRoot $manifest)"
    if ($LASTEXITCODE -ne 0) { throw "Speech packaging failed for $manifest." }
}

Write-Output "Speech package directory: $destination"
Get-ChildItem -LiteralPath $destination -Filter 'Oscova.Agents.*.nupkg' |
    ForEach-Object { Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 }
