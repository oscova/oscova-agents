[CmdletBinding()]
param([string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectDirectory '../..'))
$project = Join-Path $projectDirectory 'Oscova.Agents.Plugins.LocalSpeech.csproj'
$destination = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $repositoryRoot 'artifacts/local-speech/packages'
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null

# Keep restore and compilation settings identical for every pack of an edition.
$cpuArguments = @($project, '-c', 'Release', '-p:LocalSpeechEdition=Cpu', '-p:GeneratePackageOnBuild=false')
& dotnet build @cpuArguments
if ($LASTEXITCODE -ne 0) { throw 'CPU speech build failed.' }
& dotnet pack @cpuArguments --no-build -o $destination
if ($LASTEXITCODE -ne 0) { throw 'CPU speech packaging failed.' }

$cudaArguments = @($project, '-c', 'Release', '-p:LocalSpeechEdition=Cuda', '-p:GeneratePackageOnBuild=false',
    '--artifacts-path', (Join-Path $repositoryRoot 'artifacts/local-speech/cuda'),
    '-p:IncludeProjectNameInArtifactsPaths=true', '-p:ArtifactsProjectName=Oscova.Agents.Plugins.LocalSpeech')
& dotnet build @cudaArguments
if ($LASTEXITCODE -ne 0) { throw 'CUDA speech build failed.' }

foreach ($manifest in @('Whisper.Cuda.Windows.nuspec', 'Whisper.Cuda.Linux.nuspec', 'LocalSpeech.Cuda.nuspec')) {
    & dotnet pack @cudaArguments --no-build -o $destination "-p:NuspecFile=$(Join-Path $PSScriptRoot $manifest)"
    if ($LASTEXITCODE -ne 0) { throw "Speech packaging failed for $manifest." }
}

# Existing CPU artifact consumers still use the project's Release folder.
[xml] $projectXml = Get-Content -LiteralPath $project
$version = @($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
$cpuArchiveName = "Oscova.Agents.Plugins.LocalSpeech.$version.nupkg"
$cpuArchive = Join-Path $destination $cpuArchiveName
$legacyArchive = Join-Path $projectDirectory "bin/Release/$cpuArchiveName"
if ([IO.Path]::GetFullPath($cpuArchive) -ne [IO.Path]::GetFullPath($legacyArchive)) {
    New-Item -ItemType Directory -Path (Split-Path $legacyArchive) -Force | Out-Null
    Copy-Item -LiteralPath $cpuArchive -Destination $legacyArchive -Force
}

Write-Output "Speech package directory: $destination"
Get-ChildItem -LiteralPath $destination -Filter 'Oscova.Agents.*.nupkg' |
    ForEach-Object { Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 }
