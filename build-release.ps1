# Build and package release script for P4SyncTransfer
# Usage: Run this script from the project root directory
# Parameters:
#   -UseDocker: Build using Docker container (no local .NET install required)

param(
    [switch]$UseDocker
)

$solution = "P4Sync.sln"
$configuration = "Release"
$releaseDir = "release"
$buildOutputDir = "src\bin\$configuration\net9.0"
$packageName = "P4SyncRelease_$(Get-Date -Format 'yyyyMMdd_HHmmss').zip"
$packagePath = "$releaseDir\$packageName"

# Ensure release directory exists
if (!(Test-Path $releaseDir)) {
    New-Item -ItemType Directory -Path $releaseDir | Out-Null
}

if ($UseDocker) {
    Write-Host "Building solution in $configuration mode using Docker..."
    
    # Use Docker with .NET SDK to build
    $dockerImage = "mcr.microsoft.com/dotnet/sdk:9.0"
    $containerName = "p4sync-build-$(Get-Random)"
    
    # Run build in Docker container
    docker run --rm --name $containerName -v "${PWD}:/src" -w /src $dockerImage dotnet build $solution -c $configuration
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Docker build failed. Aborting packaging."
        exit 1
    }
} else {
    Write-Host "Building solution in $configuration mode..."
    dotnet build $solution -c $configuration
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed. Aborting packaging."
        exit 1
    }
}

Write-Host "Packaging build output..."
# Collect files: DLLs, EXEs, configs, and optionally README
$filesToInclude = @( 
    "$buildOutputDir\*.dll",
    "$buildOutputDir\*.exe",
    "$buildOutputDir\*.json",
    "$buildOutputDir\*.config",
    "README.md"
)

Compress-Archive -Path $filesToInclude -DestinationPath $packagePath -Force

Write-Host "Release package created: $packagePath"