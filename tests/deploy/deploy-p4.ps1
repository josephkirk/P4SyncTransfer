# P4Sync Podman Deployment Script for Windows
# This script sets up a complete Perforce testing environment using Podman

param(
    [switch]$Clean,
    [switch]$Build,
    [switch]$Start,
    [switch]$Stop,
    [switch]$Test,
    [switch]$EndToEndTest,
    [switch]$Help
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir

function Write-Header {
    param([string]$Message)
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "✓ $Message" -ForegroundColor Green
}

function Write-Error {
    param([string]$Message)
    Write-Host "✗ $Message" -ForegroundColor Red
}

function Show-Help {
    Write-Host "P4Sync Podman Deployment Script" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Usage: .\deploy-p4.ps1 [options]" -ForegroundColor White
    Write-Host ""
    Write-Host "Options:" -ForegroundColor White
    Write-Host "  -Clean         Remove all containers, volumes, and networks"
    Write-Host "  -Build         Build the Podman images"
    Write-Host "  -Start         Start the Perforce testing environment"
    Write-Host "  -Stop          Stop the Perforce testing environment"
    Write-Host "  -Test          Run connection tests against the environment"
    Write-Host "  -EndToEndTest  Run comprehensive end-to-end sync test"
    Write-Host "  -Help          Show this help message"
    Write-Host ""
    Write-Host "Examples:" -ForegroundColor White
    Write-Host "  .\deploy-p4.ps1 -Start    # Start the environment"
    Write-Host "  .\deploy-p4.ps1 -Clean    # Clean up everything"
    Write-Host "  .\deploy-p4.ps1 -Test     # Test connections"
}

function Test-Podman {
    try {
        $version = podman --version 2>$null
        Write-Success "Podman is installed: $version"
        return $true
    }
    catch {
        Write-Error "Podman is not installed or not in PATH"
        Write-Host "Please install Podman from: https://podman.io/getting-started/installation" -ForegroundColor Yellow
        return $false
    }
}

function Clean-Environment {
    Write-Header "Cleaning up existing environment"

    try {
        # Stop and remove containers
        podman compose -f "$scriptDir\docker-compose.yml" down 2>$null
        Write-Success "Stopped and removed containers"
    }
    catch {
        Write-Host "No running containers to clean up" -ForegroundColor Yellow
    }

    try {
        # Remove volumes
        podman volume rm p4sync_perforce-source-data 2>$null
        podman volume rm p4sync_perforce-target-data 2>$null
        Write-Success "Removed data volumes"
    }
    catch {
        Write-Host "No volumes to clean up" -ForegroundColor Yellow
    }

    try {
        # Remove networks
        podman network rm p4sync_p4sync-network 2>$null
        Write-Success "Removed networks"
    }
    catch {
        Write-Host "No networks to clean up" -ForegroundColor Yellow
    }

    Write-Success "Environment cleaned up"
}

function Build-Images {
    Write-Header "Building Podman images"

    # Note: Using pre-built Perforce images for simplicity
    Write-Host "Using pre-built Perforce images (no custom build needed)" -ForegroundColor Yellow
    Write-Success "Images ready"
}

function Start-Environment {
    Write-Header "Starting Perforce testing environment"

    # Check if already running
    $running = podman compose -f "$scriptDir\docker-compose.yml" ps 2>$null
    if ($running -and $running -notmatch "Exit|stopped") {
        Write-Host "Environment is already running" -ForegroundColor Yellow
        return
    }

    # Start the services
    Write-Host "Starting Perforce server and client containers..." -ForegroundColor Yellow
    podman compose -f "$scriptDir\docker-compose.yml" up -d

    if ($LASTEXITCODE -eq 0) {
        Write-Success "Environment started successfully"

        Write-Host ""
        Write-Host "Perforce Servers Details:" -ForegroundColor Cyan
        Write-Host "  Source Server: localhost:1666" -ForegroundColor White
        Write-Host "  Target Server: localhost:1667" -ForegroundColor White
        Write-Host "  User: admin" -ForegroundColor White
        Write-Host "  Password: admin123" -ForegroundColor White
        Write-Host ""
        Write-Host "Test Client Details:" -ForegroundColor Cyan
        Write-Host "  User: testuser" -ForegroundColor White
        Write-Host "  Workspace: testworkspace" -ForegroundColor White
        Write-Host ""
        Write-Host "Waiting for services to be ready..." -ForegroundColor Yellow

        # Wait for servers to be ready
        Start-Sleep -Seconds 30

        Write-Host "Environment is ready for testing!" -ForegroundColor Green
    }
    else {
        Write-Error "Failed to start environment"
        exit 1
    }
}

function Stop-Environment {
    Write-Header "Stopping Perforce testing environment"

    podman compose -f "$scriptDir\docker-compose.yml" stop

    if ($LASTEXITCODE -eq 0) {
        Write-Success "Environment stopped"
    }
    else {
        Write-Error "Failed to stop environment"
    }
}

function Test-Connections {
    Write-Header "Testing Perforce connections"

    # Test source server connection
    Write-Host "Testing source server connection (port 1666)..." -ForegroundColor Yellow
    try {
        $result = podman exec p4sync-source-server p4 info 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Success "Source server connection successful"
        }
        else {
            Write-Error "Source server connection failed"
        }
    }
    catch {
        Write-Error "Cannot connect to source server container"
    }

    # Test target server connection
    Write-Host "Testing target server connection (port 1667)..." -ForegroundColor Yellow
    try {
        $result = podman exec p4sync-target-server p4 info 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Success "Target server connection successful"
        }
        else {
            Write-Error "Target server connection failed"
        }
    }
    catch {
        Write-Error "Cannot connect to target server container"
    }

    # Test client connection to source server
    Write-Host "Testing client connection to source server..." -ForegroundColor Yellow
    try {
        $result = podman exec p4sync-test-client p4 info 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Success "Client connection to source server successful"
        }
        else {
            Write-Error "Client connection to source server failed"
        }
    }
    catch {
        Write-Error "Cannot connect to client container"
    }

    # Test depot contents on source server
    Write-Host "Testing depot contents on source server..." -ForegroundColor Yellow
    try {
        $files = podman exec p4sync-test-client p4 files //depot/... 2>$null
        if ($LASTEXITCODE -eq 0 -and $files) {
            Write-Success "Source depot contains files"
            Write-Host "Sample files in source depot:" -ForegroundColor Cyan
            $files | ForEach-Object { Write-Host "  $_" -ForegroundColor White }
        }
        else {
            Write-Host "Source depot is empty or not accessible" -ForegroundColor Yellow
        }
    }
    catch {
        Write-Error "Cannot access source depot contents"
    }
}

function Run-EndToEndTest {
    Write-Header "Running End-to-End Synchronization Test"

    $testScript = Join-Path $scriptDir "test-end-to-end.ps1"

    if (Test-Path $testScript) {
        Write-Host "Executing end-to-end test script..." -ForegroundColor Yellow
        & $testScript
        if ($LASTEXITCODE -eq 0) {
            Write-Success "End-to-end test completed successfully"
        } else {
            Write-Error "End-to-end test failed"
        }
    } else {
        Write-Error "End-to-end test script not found at $testScript"
    }
}

# Main execution logic
if ($Help) {
    Show-Help
    exit 0
}

if (-not (Test-Podman)) {
    exit 1
}

if ($Clean) {
    Clean-Environment
}

if ($Build) {
    Build-Images
}

if ($Start) {
    Start-Environment
}

if ($Stop) {
    Stop-Environment
}

if ($Test) {
    Test-Connections
}

if ($EndToEndTest) {
    Run-EndToEndTest
}

# If no specific action requested, show help
if (-not ($Clean -or $Build -or $Start -or $Stop -or $Test -or $EndToEndTest)) {
    Write-Host "No action specified. Use -Help for usage information." -ForegroundColor Yellow
    Show-Help
}