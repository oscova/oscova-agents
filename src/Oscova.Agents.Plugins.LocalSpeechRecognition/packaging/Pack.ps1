<#
.SYNOPSIS
    Build the local speech recognition CPU and CUDA packages.
.DESCRIPTION
    PURPOSE
      Build Oscova.Agents.Plugins.LocalSpeechRecognition and its .Cuda edition. Recognition also packages Oscova.Agents.Native.Whisper.Cuda.Windows and .Linux.
    INPUTS
      The sibling plugin project, manifests and nuspec files. Package versions come from Versions.props.
      Native and upstream dependency versions come from the plugin project. The built framework package must be available.
    ACTIONS
      Restore, build and pack both editions in Release. CUDA builds use separate artifacts to preserve CPU output.
      No package upload or model download.
    OUTPUTS
      NuGet archives in repository artifacts/nuget, or the supplied output directory. CUDA build output stays under artifacts.
    OPTIONS
      -OutputDirectory <path>: optional. Default repository artifacts/nuget. Relative paths resolve from the current directory.
      -?: display this help without building.
    EXAMPLES
      ./Pack.ps1 -?
      ./Pack.ps1 -OutputDirectory ./packages
#>
[CmdletBinding()]
param([string] $OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path $PSScriptRoot
# The Git root, so a checkout that nests this folder keeps its packages in its own artifacts folder.
$repositoryRoot = git -C $projectDirectory rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -ne 0) { $repositoryRoot = [IO.Path]::GetFullPath((Join-Path $projectDirectory '../..')) }
$project = Join-Path $projectDirectory 'Oscova.Agents.Plugins.LocalSpeechRecognition.csproj'
$destination = if ($OutputDirectory) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    Join-Path $repositoryRoot 'artifacts/nuget'
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
    '--artifacts-path', (Join-Path $repositoryRoot 'artifacts/local-speech-recognition/cuda'),
    '-p:IncludeProjectNameInArtifactsPaths=true', '-p:ArtifactsProjectName=Oscova.Agents.Plugins.LocalSpeechRecognition')
$cudaArguments = @($project, '-c', 'Release') + $cudaProperties
& dotnet restore $project @cudaProperties "-p:RestoreAdditionalProjectSources=$destination"
if ($LASTEXITCODE -ne 0) { throw 'CUDA speech restore failed.' }
& dotnet build @cudaArguments --no-restore
if ($LASTEXITCODE -ne 0) { throw 'CUDA speech build failed.' }

foreach ($manifest in @('Whisper.Cuda.Windows.nuspec', 'Whisper.Cuda.Linux.nuspec', 'LocalSpeechRecognition.Cuda.nuspec')) {
    & dotnet pack @cudaArguments --no-build --no-restore -o $destination "-p:NuspecFile=$(Join-Path $PSScriptRoot $manifest)"
    if ($LASTEXITCODE -ne 0) { throw "Speech packaging failed for $manifest." }
}

Write-Output "Speech package directory: $destination"
Get-ChildItem -LiteralPath $destination -Filter 'Oscova.Agents.*.nupkg' |
    ForEach-Object { Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 }
