#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Comprehensive integration test for P4Sync with dual Perforce servers
.DESCRIPTION
    Tests end-to-end synchronization between two Perforce servers using P4Sync
#>

param(
    [switch]$SkipCleanup,
    [switch]$Verbose
)

# Configuration
$SourcePort = "localhost:1666"
$TargetPort = "localhost:1667"
$TestConfigPath = Join-Path $PSScriptRoot "..\test-config.json"
$P4SyncBinary = Join-Path $PSScriptRoot "..\..\src\bin\Debug\net9.0\P4Sync.exe"

# Test data configuration
$TestFiles = @(
    @{ Name = "TestClass.cs"; Content = "public class TestClass { }"; ShouldSync = $true },
    @{ Name = "readme.txt"; Content = "This is a readme file"; ShouldSync = $true },
    @{ Name = "data.bin"; Content = "binary data"; ShouldSync = $false },
    @{ Name = "temp.obj"; Content = "object file"; ShouldSync = $false },
    @{ Name = "app.exe"; Content = "executable"; ShouldSync = $false }
)

function Write-TestStep {
    param([string]$Message)
    Write-Host "🔍 $Message" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "✅ $Message" -ForegroundColor Green
}

function Write-Error {
    param([string]$Message)
    Write-Host "❌ $Message" -ForegroundColor Red
}

function Build-P4SyncBinary {
    Write-TestStep "Building P4Sync binary"

    $projectPath = Join-Path $PSScriptRoot "..\..\src\P4Sync.csproj"
    
    if (-not (Test-Path $projectPath)) {
        Write-Error "P4Sync project file not found at $projectPath"
        return $false
    }

    try {
        Write-Host "Building P4Sync project..." -ForegroundColor Yellow
        $buildResult = & dotnet build $projectPath --configuration Debug --verbosity minimal 2>&1
        
        if ($LASTEXITCODE -eq 0) {
            Write-Success "P4Sync binary built successfully"
            return $true
        } else {
            Write-Error "Build failed with exit code $LASTEXITCODE"
            Write-Host "Build output:" -ForegroundColor Red
            $buildResult | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            return $false
        }
    } catch {
        Write-Error "Error building P4Sync: $($_.Exception.Message)"
        return $false
    }
}

function Test-PerforceConnection {
    param([string]$Port, [string]$Description)

    Write-TestStep "Testing connection to $Description ($Port)"
    try {
        $result = & p4 -p $Port info 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Success "Connected to $Description successfully"
            return $true
        } else {
            Write-Error "Failed to connect to $Description`: $result"
            return $false
        }
    } catch {
        Write-Error "Error connecting to $Description`: $($_.Exception.Message)"
        return $false
    }
}

function Setup-ClientWorkspaces {
    try {
        # Create target workspace directory
        $targetWorkspaceDir = Join-Path $PSScriptRoot "target-workspace"
        if (-not (Test-Path $targetWorkspaceDir)) {
            New-Item -ItemType Directory -Path $targetWorkspaceDir -Force
        }

        # Create source workspace
        Write-Host "Creating source workspace 'testworkspace'..." -ForegroundColor Yellow
        $sourceClientSpec = @"
Client: testworkspace

Update: $(Get-Date -Format "yyyy/MM/dd HH:mm:ss")
Access: $(Get-Date -Format "yyyy/MM/dd HH:mm:ss")
Owner: admin
Host: 
Description:
        Created by P4Sync end-to-end test.

Root: $PSScriptRoot

Options:        noallwrite noclobber nocompress unlocked nomodtime normdir

SubmitOptions:  submitunchanged

LineEnd:        local

View:
        //depot/... //testworkspace/...
"@
        
        $sourceClientSpec | & p4 -p $SourcePort -u admin client -i 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to create source client workspace"
            return $false
        }

        # Create target workspace
        Write-Host "Creating target workspace 'workspace'..." -ForegroundColor Yellow
        $targetClientSpec = @"
Client: workspace

Update: $(Get-Date -Format "yyyy/MM/dd HH:mm:ss")
Access: $(Get-Date -Format "yyyy/MM/dd HH:mm:ss")
Owner: admin
Host: 
Description:
        Created by P4Sync end-to-end test.

Root: $PSScriptRoot\target-workspace

Options:        noallwrite noclobber nocompress unlocked nomodtime normdir

SubmitOptions:  submitunchanged

LineEnd:        local

View:
        //project/... //workspace/...
"@
        
        $targetClientSpec | & p4 -p $TargetPort -u admin client -i 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to create target client workspace"
            return $false
        }

        Write-Success "Client workspaces created successfully"
        return $true
    } catch {
        Write-Error "Error setting up client workspaces: $($_.Exception.Message)"
        return $false
    }
}

function Setup-InitialTestData {
    try {
        # Clean up existing test files first
        Write-Host "Cleaning up existing test files..." -ForegroundColor Yellow
        $testFilesDir = Join-Path $PSScriptRoot "test-files"
        $targetWorkspaceDir = Join-Path $PSScriptRoot "target-workspace"
        
        # Clean source workspace files
        if (Test-Path $testFilesDir) {
            Get-ChildItem -Path $testFilesDir -Recurse -Force | ForEach-Object {
                if ($_.PSIsContainer) {
                    # Remove read-only attribute from directories
                    $_.Attributes = $_.Attributes -band -bnot [System.IO.FileAttributes]::ReadOnly
                } else {
                    # Remove read-only attribute from files
                    $_.Attributes = $_.Attributes -band -bnot [System.IO.FileAttributes]::ReadOnly
                }
            }
            Remove-Item -Path $testFilesDir -Recurse -Force
        }

        # Clean target workspace files
        if (Test-Path $targetWorkspaceDir) {
            Get-ChildItem -Path $targetWorkspaceDir -Recurse -Force | ForEach-Object {
                if ($_.PSIsContainer) {
                    # Remove read-only attribute from directories
                    $_.Attributes = $_.Attributes -band -bnot [System.IO.FileAttributes]::ReadOnly
                } else {
                    # Remove read-only attribute from files
                    $_.Attributes = $_.Attributes -band -bnot [System.IO.FileAttributes]::ReadOnly
                }
            }
            Remove-Item -Path $targetWorkspaceDir -Recurse -Force
        }

        # Create test-files directory
        New-Item -ItemType Directory -Path $testFilesDir -Force

        # Create test files
        Write-Host "Creating test files..." -ForegroundColor Yellow
        
        # TestClass.cs
        "public class TestClass { }" | Out-File -FilePath (Join-Path $testFilesDir "TestClass.cs") -Encoding UTF8 -Force
        
        # readme.txt
        "This is a readme file" | Out-File -FilePath (Join-Path $testFilesDir "readme.txt") -Encoding UTF8 -Force
        
        # data.bin (should be filtered out)
        "binary data" | Out-File -FilePath (Join-Path $testFilesDir "data.bin") -Encoding UTF8 -Force
        
        # Create obj directory and file
        $objDir = Join-Path $testFilesDir "obj"
        New-Item -ItemType Directory -Path $objDir -Force
        "object file" | Out-File -FilePath (Join-Path $objDir "temp.obj") -Encoding UTF8 -Force
        
        # Create bin directory and file
        $binDir = Join-Path $testFilesDir "bin"
        New-Item -ItemType Directory -Path $binDir -Force
        "executable" | Out-File -FilePath (Join-Path $binDir "app.exe") -Encoding UTF8 -Force

        # Add files to source Perforce depot
        Write-Host "Adding files to source Perforce depot..." -ForegroundColor Yellow
        $addResult = & p4 -p $SourcePort -u admin -c testworkspace add (Join-Path $testFilesDir "...") 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to add files to Perforce: $addResult"
            return $false
        }

        # Submit the files
        Write-Host "Submitting files to depot..." -ForegroundColor Yellow
        $submitResult = & p4 -p $SourcePort -u admin -c testworkspace submit -d "Initial test data submission" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to submit files to depot: $submitResult"
            return $false
        }

        Write-Success "Initial test data created successfully"
        return $true
    } catch {
        Write-Error "Error setting up initial test data: $($_.Exception.Message)"
        return $false
    }
}

function Start-TestEnvironment {
    Write-TestStep "Starting dual-server Podman environment"

    Push-Location $PSScriptRoot

    try {
        # Clean up existing volumes to ensure clean test state
        Write-TestStep "Cleaning up existing Podman volumes"
        podman volume rm -f perforce-source-data 2>$null
        podman volume rm -f perforce-target-data 2>$null
        podman volume prune -f 2>$null
        Write-Success "Cleaned up existing volumes"

        # Start the containers
        & podman compose up -d

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to start Podman environment"
            return $false
        }

        # Wait for servers to be ready
        Write-Host "Waiting for Perforce servers to initialize..." -ForegroundColor Yellow
        Start-Sleep -Seconds 30

        # Test connections
        $sourceReady = Test-PerforceConnection $SourcePort "source server"
        $targetReady = Test-PerforceConnection $TargetPort "target server"

        if ($sourceReady -and $targetReady) {
            # Set up client workspaces for P4Sync
            Write-TestStep "Setting up client workspaces"
            $workspaceSetup = Setup-ClientWorkspaces
            if (-not $workspaceSetup) {
                Write-Error "Failed to set up client workspaces"
                return $false
            }
            Write-Success "Client workspaces set up successfully"

            # Create initial test data
            Write-TestStep "Creating initial test data"
            $dataSetup = Setup-InitialTestData
            if (-not $dataSetup) {
                Write-Error "Failed to set up initial test data"
                return $false
            }
            Write-Success "Initial test data created successfully"

            Write-Success "Dual-server environment is ready"
            return $true
        } else {
            Write-Error "One or more servers failed to start properly"
            return $false
        }

    } finally {
        Pop-Location
    }
}

function Test-P4SyncBinary {
    Write-TestStep "Testing P4Sync binary availability"

    if (Test-Path $P4SyncBinary) {
        Write-Success "P4Sync binary found at $P4SyncBinary"
        return $true
    } else {
        Write-Error "P4Sync binary not found at $P4SyncBinary"
        return $false
    }
}

function Test-ConfigurationFile {
    Write-TestStep "Validating test configuration file"

    if (Test-Path $TestConfigPath) {
        try {
            $config = Get-Content $TestConfigPath | ConvertFrom-Json
            Write-Success "Configuration file is valid JSON"

            # Validate required fields
            if ($config.SyncProfiles -and $config.SyncProfiles[0].Source -and $config.SyncProfiles[0].Target) {
                Write-Success "Configuration contains required sync profile structure"
                return $true
            } else {
                Write-Error "Configuration missing required sync profile fields"
                return $false
            }
        } catch {
            Write-Error "Configuration file is not valid JSON: $($_.Exception.Message)"
            return $false
        }
    } else {
        Write-Error "Configuration file not found at $TestConfigPath"
        return $false
    }
}

function Invoke-EndToEndSyncTest {
    Write-TestStep "Running end-to-end synchronization test"

    try {
        # Check if P4Sync binary exists and is executable
        if (-not (Test-Path $P4SyncBinary)) {
            Write-Error "P4Sync binary not found at $P4SyncBinary"
            return $false
        }

        Write-Host "P4Sync binary path: $P4SyncBinary" -ForegroundColor Gray
        Write-Host "Test config path: $TestConfigPath" -ForegroundColor Gray

        # Run P4Sync with timeout and detailed logging
        Write-Host "Executing P4Sync with verbose output..." -ForegroundColor Yellow

        # Start the process asynchronously so we can monitor it
        $process = Start-Process -FilePath $P4SyncBinary -ArgumentList "sync", "--config", $TestConfigPath -NoNewWindow -PassThru -RedirectStandardOutput "p4sync_output.log" -RedirectStandardError "p4sync_error.log"

        Write-Host "P4Sync process started with ID: $($process.Id)" -ForegroundColor Gray

        # Wait for the process with timeout
        $timeout = 120 # 2 minutes timeout
        $startTime = Get-Date

        while (-not $process.HasExited -and ((Get-Date) - $startTime).TotalSeconds -lt $timeout) {
            Write-Host "P4Sync still running... ($([math]::Round(((Get-Date) - $startTime).TotalSeconds)))s elapsed" -ForegroundColor Gray
            Start-Sleep -Seconds 5

            # Check if we have any output
            if (Test-Path "p4sync_output.log") {
                $output = Get-Content "p4sync_output.log" -Tail 3
                if ($output) {
                    Write-Host "Recent P4Sync output:" -ForegroundColor Gray
                    $output | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
                }
            }

            if (Test-Path "p4sync_error.log") {
                $errorOutput = Get-Content "p4sync_error.log" -Tail 3
                if ($errorOutput) {
                    Write-Host "Recent P4Sync errors:" -ForegroundColor Yellow
                    $errorOutput | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
                }
            }
        }

        # Check if process completed or timed out
        if (-not $process.HasExited) {
            Write-Error "P4Sync process timed out after $timeout seconds"
            $process.Kill()
            return $false
        }

        $exitCode = $process.ExitCode
        Write-Host "P4Sync process completed with exit code: $exitCode" -ForegroundColor Gray

        # Read the complete output
        if (Test-Path "p4sync_output.log") {
            $fullOutput = Get-Content "p4sync_output.log"
            Write-Host "P4Sync Standard Output:" -ForegroundColor Gray
            $fullOutput | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
        }

        if (Test-Path "p4sync_error.log") {
            $fullError = Get-Content "p4sync_error.log"
            if ($fullError) {
                Write-Host "P4Sync Error Output:" -ForegroundColor Red
                $fullError | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            }
        }

        if ($exitCode -eq 0) {
            Write-Success "P4Sync executed successfully"
            return $true
        } else {
            Write-Error "P4Sync execution failed with exit code $exitCode"
            return $false
        }
    } catch {
        Write-Error "Error running P4Sync: $($_.Exception.Message)"
        return $false
    }
}

function Test-SyncResults {
    Write-TestStep "Verifying synchronization results"

    $allTestsPassed = $true

    # Check source server files
    Write-Host "Checking source server files..." -ForegroundColor Yellow
    $sourceFiles = & p4 -p $SourcePort files //depot/... 2>&1
    Write-Host $sourceFiles
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Source server has $($sourceFiles.Count) files"
        if ($Verbose) {
            $sourceFiles | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
        }
    } else {
        Write-Error "Failed to list source server files"
        $allTestsPassed = $false
    }

    # Check target server files
    Write-Host "Checking target server files..." -ForegroundColor Yellow
    $targetFiles = & p4 -p $TargetPort files //project/... 2>&1 | Where-Object { $_ -notmatch "- delete " }
    Write-Host $targetFiles
    if ($LASTEXITCODE -eq 0) {
        Write-Success "Target server has $($targetFiles.Count) files"

        # Verify sync filter worked correctly
        $expectedFiles = $TestFiles | Where-Object { $_.ShouldSync } | ForEach-Object { "//project/test-files/$($_.Name)" }
        $unexpectedFiles = $TestFiles | Where-Object { -not $_.ShouldSync } | ForEach-Object { "//project/test-files/$($_.Name)" }

        foreach ($expectedFile in $expectedFiles) {
            if ($targetFiles -match [regex]::Escape($expectedFile)) {
                Write-Success "Expected file found on target: $expectedFile"
            } else {
                Write-Error "Expected file missing on target: $expectedFile"
                $allTestsPassed = $false
            }
        }

        foreach ($unexpectedFile in $unexpectedFiles) {
            if ($targetFiles -match [regex]::Escape($unexpectedFile)) {
                Write-Error "Unexpected file found on target (should have been filtered): $unexpectedFile"
                $allTestsPassed = $false
            } else {
                Write-Success "File correctly filtered out: $unexpectedFile"
            }
        }

    } else {
        Write-Error "Failed to list target server files"
        $allTestsPassed = $false
    }

    return $allTestsPassed
}

function Test-EditFileOperation {
    Write-TestStep "Testing edit file operation and performance with large files"

    try {
        # Set up client for source server operations
        Write-Host "Setting up client for source server operations..." -ForegroundColor Yellow
        $clientSetup = & p4 -p $SourcePort -u admin -c testworkspace info 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to set up client for source server: $clientSetup"
            return $false
        }

        # Edit an existing file
        $fileToEdit = "//depot/test-files/TestClass.cs"
        Write-Host "Editing file: $fileToEdit" -ForegroundColor Yellow

        # Open file for edit
        $editResult = & p4 -p $SourcePort -u admin -c testworkspace edit $fileToEdit 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to open file for edit: $editResult"
            return $false
        }

        # Modify the file content
        $localFilePath = Join-Path $PSScriptRoot "test-files\TestClass.cs"
        $newContent = @"
public class TestClass {
    // Modified content for edit test
    public string ModifiedProperty { get; set; } = "Edited";

    public void ModifiedMethod() {
        Console.WriteLine("This file has been modified");
    }
}
"@
        $newContent | Out-File -FilePath $localFilePath -Encoding UTF8 -Force

        # Submit the edit
        Write-Host "Submitting file edit..." -ForegroundColor Yellow
        $submitResult = & p4 -p $SourcePort -u admin -c testworkspace submit -f submitunchanged -d "Edit test file" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to submit edit: $submitResult"
            return $false
        }

        Write-Success "File edited successfully on source server"

        # Run sync to propagate edit
        Write-Host "Running sync to propagate edit operation..." -ForegroundColor Yellow
        $syncResult = Invoke-EndToEndSyncTest
        if (-not $syncResult) {
            Write-Error "Sync failed after edit operation"
            return $false
        }

        # Verify the edit was propagated to target
        Write-Host "Verifying edit was propagated to target..." -ForegroundColor Yellow
        $targetFileContent = & p4 -p $TargetPort -u admin -c workspace print "//project/test-files/TestClass.cs" 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to get target file content"
            return $false
        }

        if ($targetFileContent -match "ModifiedProperty" -and $targetFileContent -match "ModifiedMethod") {
            Write-Success "File edit successfully propagated to target server"
        } else {
            Write-Error "File edit was not propagated correctly to target server"
            return $false
        }

        # Test with large file for performance
        Write-Host "Creating 1GB test file for performance testing..." -ForegroundColor Yellow
        $largeFilePath = Join-Path $PSScriptRoot "test-files\large-test-file.dat"

        # Create a 1GB file (using fsutil for efficiency)
        $fsutilResult = & fsutil file createnew $largeFilePath 1073741824 2>&1  # 1GB = 1073741824 bytes
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to create large test file: $fsutilResult"
            return $false
        }

        # Add the large file to Perforce
        Write-Host "Adding large file to Perforce..." -ForegroundColor Yellow
        $addResult = & p4 -p $SourcePort -u admin -c testworkspace add $largeFilePath 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to add large file to Perforce: $addResult"
            return $false
        }

        # Submit the large file
        Write-Host "Submitting large file..." -ForegroundColor Yellow
        $submitStartTime = Get-Date
        $submitResult = & p4 -p $SourcePort -u admin -c testworkspace submit -f submitunchanged -d "Add large test file for performance testing" 2>&1
        $submitEndTime = Get-Date
        $submitDuration = $submitEndTime - $submitStartTime

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to submit large file: $submitResult"
            return $false
        }

        Write-Host "Large file submitted in $($submitDuration.TotalSeconds) seconds" -ForegroundColor Cyan

        # Run sync with large file
        Write-Host "Running sync with large file..." -ForegroundColor Yellow
        $syncStartTime = Get-Date
        $syncResult = Invoke-EndToEndSyncTest
        $syncEndTime = Get-Date
        $syncDuration = $syncEndTime - $syncStartTime

        if (-not $syncResult) {
            Write-Error "Sync failed with large file"
            return $false
        }

        Write-Host "Large file sync completed in $($syncDuration.TotalSeconds) seconds" -ForegroundColor Cyan

        # Verify large file was synced
        Write-Host "Verifying large file was synced to target..." -ForegroundColor Yellow
        $targetLargeFile = & p4 -p $TargetPort -u admin -c workspace files "//project/test-files/large-test-file.dat" 2>&1
        if ($LASTEXITCODE -ne 0 -or -not ($targetLargeFile -match "large-test-file.dat")) {
            Write-Error "Large file was not synced to target server"
            return $false
        }

        Write-Success "Large file successfully synced to target server"
        Write-Host "Performance metrics:" -ForegroundColor Cyan
        Write-Host "  - File submission time: $($submitDuration.TotalSeconds) seconds" -ForegroundColor Cyan
        Write-Host "  - Sync time: $($syncDuration.TotalSeconds) seconds" -ForegroundColor Cyan

        return $true

    } catch {
        Write-Error "Error testing edit file operation: $($_.Exception.Message)"
        return $false
    }
}

function Stop-TestEnvironment {
    Write-TestStep "Stopping test environment"

    Push-Location $PSScriptRoot

    try {
        podman compose down
        podman volume prune -f 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Success "Test environment stopped successfully"
        } else {
            Write-Error "Failed to stop test environment"
        }
    } finally {
        Pop-Location
    }
}

function Test-DeleteFileOperation {
    Write-TestStep "Testing delete file operation on source server"

    try {
        # Set up client for source server operations
        Write-Host "Setting up client for source server operations..." -ForegroundColor Yellow
        $clientSetup = & p4 -p $SourcePort -u admin -c testworkspace info 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to set up client for source server: $clientSetup"
            return $false
        }

        # Check if file is already opened for delete
        $openedFiles = & p4 -p $SourcePort -u admin -c testworkspace opened 2>&1
        $fileToDelete = "//depot/test-files/readme.txt"
        
        
        if ($openedFiles -match [regex]::Escape($fileToDelete) -and $openedFiles -match "delete") {
            Write-Host "File is already opened for delete, submitting existing change..." -ForegroundColor Yellow
            $submitResult = & p4 -p $SourcePort -u admin -c testworkspace submit -f submitunchanged -d "Delete test file" 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to submit existing delete: $submitResult"
                return $false
            }
        } else {
            # Delete the file
            Write-Host "Deleting file from source: $fileToDelete" -ForegroundColor Yellow
            $deleteResult = & p4 -p $SourcePort -u admin -c testworkspace delete -f $fileToDelete 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to delete file on source server: $deleteResult"
                return $false
            }

            # Submit the delete
            Write-Host "Checking for pending changes..." -ForegroundColor Gray
            $pendingFiles = & p4 -p $SourcePort -u admin -c testworkspace opened 2>&1
            if ($LASTEXITCODE -eq 0 -and $pendingFiles -match "delete") {
                Write-Host "Found pending delete operations, submitting..." -ForegroundColor Gray
                $submitResult = & p4 -p $SourcePort -u admin -c testworkspace submit -f submitunchanged -d "Delete test file" 2>&1
                if ($LASTEXITCODE -ne 0) {
                    Write-Error "Failed to submit delete: $submitResult"
                    return $false
                }
            } else {
                Write-Host "No pending delete operations found" -ForegroundColor Yellow
            }
        }

        Write-Success "File deleted successfully on source server"

        # Run sync again to test delete propagation
        Write-Host "Running sync to propagate delete operation..." -ForegroundColor Yellow
        $syncResult = Invoke-EndToEndSyncTest
        if (-not $syncResult) {
            Write-Error "Sync failed after delete operation"
            return $false
        }

        # Verify the file was deleted on target
        Write-Host "Verifying delete was propagated to target..." -ForegroundColor Yellow
        $targetFiles = & p4 -p $TargetPort -u admin -c workspace files //project/... 2>&1 | Where-Object { $_ -notmatch "- delete " }
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to list target server files after delete"
            return $false
        }

        $targetFileToCheck = "//project/test-files/readme.txt"
        if ($targetFiles -match [regex]::Escape($targetFileToCheck)) {
            Write-Error "File still exists on target server after delete sync: $targetFileToCheck"
            return $false
        } else {
            Write-Success "File successfully deleted from target server"
        }

        return $true

    } catch {
        Write-Error "Error during delete file test: $($_.Exception.Message)"
        return $false
    }
}

function Test-MoveRenameFileOperation {
    Write-TestStep "Testing move/rename file operation on source server"

    try {
        # Set up client for source server operations
        Write-Host "Setting up client for source server operations..." -ForegroundColor Yellow
        $clientSetup = & p4 -p $SourcePort -u admin -c testworkspace info 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to set up client for source server: $clientSetup"
            return $false
        }

        # First, create a new file to move
        $originalFile = "//depot/test-files/move-test.txt"
        $movedFile = "//depot/test-files/moved-file.txt"
        $localFilePath = Join-Path $PSScriptRoot "test-files" "move-test.txt"

        Write-Host "Creating file to move: $originalFile" -ForegroundColor Yellow

        # Clean up any existing move-test.txt file
        if (Test-Path $localFilePath) {
            try {
                (Get-Item $localFilePath).Attributes = (Get-Item $localFilePath).Attributes -band -bnot [System.IO.FileAttributes]::ReadOnly
                Remove-Item $localFilePath -Force
            } catch {
                Write-Host "Warning: Could not remove existing move-test.txt: $($_.Exception.Message)" -ForegroundColor Yellow
            }
        }

        # Create the local file first
        if (-not (Test-Path (Join-Path $PSScriptRoot "test-files"))) {
            New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot "test-files") -Force
        }

        "This is a test file for move/rename operations" | Out-File -FilePath $localFilePath -Encoding UTF8 -Force

        # Add the file to Perforce
        $createResult = & p4 -p $SourcePort -u admin -c testworkspace add $localFilePath 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to add file to Perforce: $createResult"
            return $false
        }

        # Submit the new file
        Write-Host "Checking for pending add operations..." -ForegroundColor Gray
        $pendingFiles = & p4 -p $SourcePort -u admin -c testworkspace opened 2>&1
        if ($LASTEXITCODE -eq 0 -and $pendingFiles -match "add") {
            Write-Host "Found pending add operations, submitting..." -ForegroundColor Gray
            $submitResult = & p4 -p $SourcePort -u admin -c testworkspace submit -f submitunchanged -d "Add test file for move operation" 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to submit new file: $submitResult"
                return $false
            }
        } else {
            Write-Host "No pending add operations found" -ForegroundColor Yellow
        }

        Write-Success "Test file created successfully"

        # Move/rename the file
        Write-Host "Opening file for edit before move..." -ForegroundColor Yellow
        $editResult = & p4 -p $SourcePort -u admin -c testworkspace edit $originalFile 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to open file for edit: $editResult"
            return $false
        }

        Write-Host "Moving file from $originalFile to $movedFile" -ForegroundColor Yellow
        $moveResult = & p4 -p $SourcePort -u admin -c testworkspace move -f $originalFile $movedFile 2>&1
        Write-Host "Move command result: $moveResult" -ForegroundColor Gray
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to move file on source server: $moveResult"
            return $false
        }

        # Submit the move
        Write-Host "Checking for pending operations after move..." -ForegroundColor Gray
        $pendingFiles = & p4 -p $SourcePort -u admin -c testworkspace opened 2>&1
        Write-Host "Pending files output:" -ForegroundColor Gray
        $pendingFiles | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
        
        if ($LASTEXITCODE -eq 0 -and ($pendingFiles -match "add" -or $pendingFiles -match "delete")) {
            Write-Host "Found pending add/delete operations from move, submitting..." -ForegroundColor Gray
            $submitResult = & p4 -p $SourcePort -u admin -c testworkspace submit -f submitunchanged -d "Move test file" 2>&1
            Write-Host "Submit result: $submitResult" -ForegroundColor Gray
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Failed to submit move: $submitResult"
                return $false
            }
        } else {
            Write-Host "No pending operations found after move, attempting submit anyway..." -ForegroundColor Yellow
            # Check if the move was already submitted or if we need to force submit
            $submitResult = & p4 -p $SourcePort -u admin -c testworkspace submit -f submitunchanged -d "Move test file" 2>&1
            Write-Host "Forced submit result: $submitResult" -ForegroundColor Gray
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Submit failed, but continuing..." -ForegroundColor Yellow
            }
        }

        Write-Success "File moved successfully on source server"

        # Verify the move on source depot
        Write-Host "Verifying move on source depot..." -ForegroundColor Yellow
        $sourceFiles = & p4 -p $SourcePort -u admin -c testworkspace files //depot/test-files/... 2>&1
        Write-Host "Source files after move:" -ForegroundColor Gray
        $sourceFiles | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
        
        $sourceOriginalLine = $sourceFiles | Where-Object { $_ -match [regex]::Escape("//depot/test-files/move-test.txt") }
        $sourceMovedLine = $sourceFiles | Where-Object { $_ -match [regex]::Escape("//depot/test-files/moved-file.txt") }
        
        if ($sourceMovedLine -and $sourceMovedLine -match "move/add" -and $sourceOriginalLine -and $sourceOriginalLine -match "move/delete") {
            Write-Success "Move confirmed on source depot"
        } else {
            Write-Error "Move not properly recorded on source depot"
            Write-Host "Original file line: $sourceOriginalLine" -ForegroundColor Red
            Write-Host "Moved file line: $sourceMovedLine" -ForegroundColor Red
            return $false
        }

        # Run sync again to test move propagation
        Write-Host "Running sync to propagate move operation..." -ForegroundColor Yellow
        $syncResult = Invoke-EndToEndSyncTest
        if (-not $syncResult) {
            Write-Error "Sync failed after move operation"
            return $false
        }

        # Verify the file was moved on target
        Write-Host "Verifying move was propagated to target..." -ForegroundColor Yellow
        $targetFiles = & p4 -p $TargetPort -u admin -c workspace files //project/... 2>&1
        Write-Host "Target files after move:" -ForegroundColor Gray
        $targetFiles | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to list target server files after move"
            return $false
        }

        # Check that the moved file exists and original doesn't (on target, paths are translated to //project)
        $targetOriginalFile = "//project/test-files/move-test.txt"
        $targetMovedFile = "//project/test-files/moved-file.txt"
        $movedExists = $targetFiles -match [regex]::Escape($targetMovedFile)
        $originalExists = $targetFiles -match [regex]::Escape($targetOriginalFile)

        if ($movedExists -and -not $originalExists) {
            Write-Success "File successfully moved on target server"
            return $true
        } elseif (-not $movedExists) {
            Write-Error "Moved file does not exist on target server: $targetMovedFile"
            return $false
        } else {
            Write-Error "Original file still exists on target server after move: $targetOriginalFile"
            return $false
        }

    } catch {
        Write-Error "Error during move/rename file test: $($_.Exception.Message)"
        return $false
    } finally {
        # Clean up temp file
        if (Test-Path (Join-Path $PSScriptRoot "temp-move-file.txt")) {
            Remove-Item (Join-Path $PSScriptRoot "temp-move-file.txt") -Force
        }
    }
}

# Main test execution
Write-Host "🚀 Starting P4Sync End-to-End Integration Test" -ForegroundColor Magenta
Write-Host "=" * 50 -ForegroundColor Magenta

# Clean up logs folder from previous runs
Write-TestStep "Cleaning up logs folders"
$logsPaths = @(
    Join-Path $PSScriptRoot "logs"    # logs in test directory
    Join-Path $PSScriptRoot "..\..\logs"  # logs in root directory
)

$cleanedAny = $false
foreach ($logsPath in $logsPaths) {
    if (Test-Path $logsPath) {
        Remove-Item -Path $logsPath -Recurse -Force
        Write-Success "Cleaned up logs folder: $logsPath"
        $cleanedAny = $true
    }
}

if (-not $cleanedAny) {
    Write-Success "No logs folders to clean up"
}

$testResults = @()

# Test 0: Build P4Sync Binary
$testResults += @{ Name = "Build P4Sync Binary"; Result = Build-P4SyncBinary }

# Test 1: Environment Setup
$testResults += @{ Name = "Environment Setup"; Result = Start-TestEnvironment }

# Test 2: Binary Availability
$testResults += @{ Name = "P4Sync Binary"; Result = Test-P4SyncBinary }

# Test 3: Configuration Validation
$testResults += @{ Name = "Configuration File"; Result = Test-ConfigurationFile }

# Test 4: End-to-End Sync
$testResults += @{ Name = "End-to-End Sync"; Result = Invoke-EndToEndSyncTest }

# Test 5: Results Verification
$testResults += @{ Name = "Sync Results"; Result = Test-SyncResults }

# Test 6: Edit File Operation and Performance
$testResults += @{ Name = "Edit File Operation"; Result = Test-EditFileOperation }

# Test 7: Delete File Operation
$testResults += @{ Name = "Delete File Operation"; Result = Test-DeleteFileOperation }

# Test 8: Move/Rename File Operation
$testResults += @{ Name = "Move/Rename File Operation"; Result = Test-MoveRenameFileOperation }

# Cleanup
if (-not $SkipCleanup) {
    Stop-TestEnvironment
}

# Summary
Write-Host "`n📊 Test Results Summary" -ForegroundColor Magenta
Write-Host "=" * 30 -ForegroundColor Magenta

$passedTests = 0
$totalTests = $testResults.Count

foreach ($test in $testResults) {
    $status = if ($test.Result) { "✅ PASS" } else { "❌ FAIL" }
    Write-Host "$status $($test.Name)"
    if ($test.Result) { $passedTests++ }
}

Write-Host "`nOverall Result: $($passedTests)/$totalTests tests passed" -ForegroundColor $(if ($passedTests -eq $totalTests) { "Green" } else { "Red" })

if ($passedTests -eq $totalTests) {
    Write-Success "All tests passed! P4Sync end-to-end synchronization is working correctly, including edit operations, performance testing, delete and move/rename operations."
    exit 0
} else {
    Write-Error "Some tests failed. Check the output above for details."
    exit 1
}