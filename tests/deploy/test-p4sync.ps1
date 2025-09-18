# P4Sync Integration Test Script
# Tests P4Sync functionality against the Podman Perforce environment

param(
    [string]$ConfigFile = "test-config.json",
    [switch]$Verbose,
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
    Write-Host "P4Sync Integration Test Script" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Usage: .\test-p4sync.ps1 [options]" -ForegroundColor White
    Write-Host ""
    Write-Host "Options:" -ForegroundColor White
    Write-Host "  -ConfigFile <file>    Configuration file to use (default: test-config.json)"
    Write-Host "  -Verbose              Enable verbose output"
    Write-Host "  -Help                 Show this help message"
    Write-Host ""
    Write-Host "Examples:" -ForegroundColor White
    Write-Host "  .\test-p4sync.ps1                           # Run with default config"
    Write-Host "  .\test-p4sync.ps1 -ConfigFile custom.json   # Use custom config"
    Write-Host "  .\test-p4sync.ps1 -Verbose                  # Verbose output"
}

function Test-PerforceEnvironment {
    Write-Header "Testing Perforce Environment"

    # Test server connectivity by checking if port is open
    try {
        $connection = Test-NetConnection -ComputerName localhost -Port 1666 -ErrorAction Stop
        if ($connection.TcpTestSucceeded) {
            Write-Success "Perforce server port 1666 is accessible"
            if ($Verbose) {
                Write-Host "Connection details:" -ForegroundColor Gray
                Write-Host "  ComputerName: $($connection.ComputerName)" -ForegroundColor Gray
                Write-Host "  RemoteAddress: $($connection.RemoteAddress)" -ForegroundColor Gray
                Write-Host "  RemotePort: $($connection.RemotePort)" -ForegroundColor Gray
            }
        }
        else {
            Write-Error "Cannot connect to Perforce server on port 1666"
            return $false
        }
    }
    catch {
        Write-Error "Failed to test Perforce server connection: $($_.Exception.Message)"
        return $false
    }

    # Test if server container is running
    try {
        $containerStatus = podman ps --filter "name=p4sync-test-server" --format "{{.Status}}" 2>$null
        if ($containerStatus -and $containerStatus -notmatch "Exit|stopped") {
            Write-Success "Perforce server container is running"
        }
        else {
            Write-Error "Perforce server container is not running"
            return $false
        }
    }
    catch {
        Write-Error "Cannot check container status"
        return $false
    }

    return $true
}

function Test-P4SyncBinary {
    Write-Header "Testing P4Sync Binary"

    $p4syncPath = Join-Path $projectRoot "src\bin\Debug\net10.0\P4Sync.exe"

    if (-not (Test-Path $p4syncPath)) {
        Write-Error "P4Sync binary not found at: $p4syncPath"
        Write-Host "Build the project first: dotnet build" -ForegroundColor Yellow
        return $false
    }

    Write-Success "P4Sync binary found"

    # Test binary execution
    try {
        & $p4syncPath "--version" 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Success "P4Sync binary executes successfully"
        }
        else {
            Write-Error "P4Sync binary execution failed"
            return $false
        }
    }
    catch {
        Write-Error "Cannot execute P4Sync binary"
        return $false
    }

    return $true
}

function Test-Configuration {
    param([string]$ConfigPath)

    Write-Header "Testing Configuration File"

    $fullConfigPath = Join-Path $scriptDir $ConfigPath

    if (-not (Test-Path $fullConfigPath)) {
        Write-Error "Configuration file not found: $fullConfigPath"
        return $false
    }

    Write-Success "Configuration file found"

    # Validate JSON syntax
    try {
        $config = Get-Content $fullConfigPath -Raw | ConvertFrom-Json
        Write-Success "Configuration JSON is valid"
    }
    catch {
        Write-Error "Configuration JSON is invalid: $($_.Exception.Message)"
        return $false
    }

    # Validate required fields
    if (-not $config.SyncProfiles) {
        Write-Error "Configuration missing SyncProfiles"
        return $false
    }

    if ($config.SyncProfiles.Count -eq 0) {
        Write-Error "Configuration has no sync profiles"
        return $false
    }

    Write-Success "Configuration has $($config.SyncProfiles.Count) sync profile(s)"

    # Validate each profile
    foreach ($syncProfile in $config.SyncProfiles) {
        if (-not $syncProfile.Name) {
            Write-Error "Profile missing Name field"
            return $false
        }

        if (-not $syncProfile.Source -or -not $syncProfile.Target) {
            Write-Error "Profile '$($syncProfile.Name)' missing Source or Target configuration"
            return $false
        }

        if (-not $syncProfile.SyncFilter -or $syncProfile.SyncFilter.Count -eq 0) {
            Write-Error "Profile '$($syncProfile.Name)' missing SyncFilter"
            return $false
        }

        Write-Success "Profile '$($syncProfile.Name)' is valid"
    }

    return $true
}

function Invoke-IntegrationTest {
    param([string]$ConfigPath)

    Write-Header "Running Integration Test"

    $fullConfigPath = Join-Path $scriptDir $ConfigPath
    $p4syncPath = Join-Path $projectRoot "src\bin\Debug\net10.0\P4Sync.exe"

    Write-Host "Running P4Sync with configuration: $ConfigPath" -ForegroundColor Yellow

    try {
        $startTime = Get-Date

        if ($Verbose) {
            & $p4syncPath $fullConfigPath
        }
        else {
            $output = & $p4syncPath $fullConfigPath 2>&1
        }

        $endTime = Get-Date
        $duration = $endTime - $startTime

        if ($LASTEXITCODE -eq 0) {
            Write-Success "P4Sync completed successfully in $($duration.TotalSeconds) seconds"

            if ($Verbose -and $output) {
                Write-Host "Output:" -ForegroundColor Gray
                $output | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
            }
        }
        else {
            Write-Error "P4Sync failed with exit code $LASTEXITCODE"

            if ($output) {
                Write-Host "Error Output:" -ForegroundColor Red
                $output | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            }

            return $false
        }
    }
    catch {
        Write-Error "Failed to run P4Sync: $($_.Exception.Message)"
        return $false
    }

    return $true
}

# Main execution
if ($Help) {
    Show-Help
    exit 0
}

Write-Header "P4Sync Integration Test Suite"
Write-Host "Project Root: $projectRoot" -ForegroundColor Gray
Write-Host "Config File: $ConfigFile" -ForegroundColor Gray
Write-Host ""

$allTestsPassed = $true

# Test 1: Perforce Environment
if (-not (Test-PerforceEnvironment)) {
    $allTestsPassed = $false
}

# Test 2: P4Sync Binary
if (-not (Test-P4SyncBinary)) {
    $allTestsPassed = $false
}

# Test 3: Configuration
if (-not (Test-Configuration -ConfigPath $ConfigFile)) {
    $allTestsPassed = $false
}

# Test 4: Integration Test (only if all previous tests passed)
if ($allTestsPassed) {
    if (-not (Invoke-IntegrationTest -ConfigPath $ConfigFile)) {
        $allTestsPassed = $false
    }
}
else {
    Write-Host "Skipping integration test due to previous failures" -ForegroundColor Yellow
}

# Summary
Write-Host ""
if ($allTestsPassed) {
    Write-Success "All tests passed! P4Sync is ready for production use."
    exit 0
}
else {
    Write-Error "Some tests failed. Please review the output above."
    exit 1
}