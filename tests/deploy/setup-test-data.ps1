$SourcePort = "localhost:1666"
$TestFilesDir = "g:\Projects\Dev\P4Sync\P4Sync\deploy\test-files"

# Clean up existing test files first
Write-Host "Cleaning up existing test files..." -ForegroundColor Yellow
if (Test-Path $TestFilesDir) {
    Remove-Item -Path $TestFilesDir -Recurse -Force
}

# Create test-files directory
New-Item -ItemType Directory -Path $TestFilesDir -Force

# Create test files
Write-Host "Creating test files..." -ForegroundColor Yellow

# TestClass.cs
"public class TestClass { }" | Out-File -FilePath (Join-Path $TestFilesDir "TestClass.cs") -Encoding UTF8 -Force

# readme.txt
"This is a readme file" | Out-File -FilePath (Join-Path $TestFilesDir "readme.txt") -Encoding UTF8 -Force

# data.bin (should be filtered out)
"binary data" | Out-File -FilePath (Join-Path $TestFilesDir "data.bin") -Encoding UTF8 -Force

# Create obj directory and file
$objDir = Join-Path $TestFilesDir "obj"
New-Item -ItemType Directory -Path $objDir -Force
"object file" | Out-File -FilePath (Join-Path $objDir "temp.obj") -Encoding UTF8 -Force

# Create bin directory and file
$binDir = Join-Path $TestFilesDir "bin"
New-Item -ItemType Directory -Path $binDir -Force
"executable" | Out-File -FilePath (Join-Path $binDir "app.exe") -Encoding UTF8 -Force

Write-Host "Creating workspace..." -ForegroundColor Yellow

# Create workspace using temporary file
$clientSpec = @"
Client: testworkspace

Update: $(Get-Date -Format "yyyy/MM/dd HH:mm:ss")
Access: $(Get-Date -Format "yyyy/MM/dd HH:mm:ss")
Owner: admin
Host: 
Description:
        Created by P4Sync end-to-end test.

Root: g:\Projects\Dev\P4Sync\P4Sync\deploy

Options:        noallwrite noclobber nocompress unlocked nomodtime normdir

SubmitOptions:  submitunchanged

LineEnd:        local

View:
        //depot/... //testworkspace/...
"@

$tempFile = [System.IO.Path]::GetTempFileName()
$clientSpec | Out-File -FilePath $tempFile -Encoding UTF8
Get-Content $tempFile | & p4 -p $SourcePort -u admin client -i
Remove-Item $tempFile

Write-Host "Adding files to depot..." -ForegroundColor Yellow
& p4 -p $SourcePort -u admin -c testworkspace add "$TestFilesDir\..."

Write-Host "Submitting files..." -ForegroundColor Yellow
& p4 -p $SourcePort -u admin -c testworkspace submit -d "Initial test data"

Write-Host "Verifying files in depot..." -ForegroundColor Yellow
& p4 -p $SourcePort -u admin files //depot/...