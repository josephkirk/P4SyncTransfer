# P4Sync Perforce Testing Environment

This directory contains a complete Podman-based deployment for testing P4Sync with real Perforce servers.

## Prerequisites

- **Podman** installed and configured
- **podman-compose** plugin installed
- **PowerShell** (for Windows deployment script)

## Quick Start

1. **Start the environment:**
   ```powershell
   .\deploy-p4.ps1 -Start
   ```

2. **Test the connections:**
   ```powershell
   .\deploy-p4.ps1 -Test
   ```

3. **Stop when done:**
   ```powershell
   .\deploy-p4.ps1 -Stop
   ```

## Environment Details

### Perforce Server
- **Container**: `p4sync-test-server`
- **Port**: `localhost:1666`
- **Admin User**: `admin`
- **Admin Password**: `admin123`
- **Depot**: `//depot/...`

### Test Client
- **Container**: `p4sync-test-client`
- **User**: `testuser`
- **Workspace**: `testworkspace`
- **Working Directory**: `/workspace`

## Available Commands

```powershell
# Show help
.\deploy-p4.ps1 -Help

# Start the testing environment
.\deploy-p4.ps1 -Start

# Stop the testing environment
.\deploy-p4.ps1 -Stop

# Test connections and depot contents
.\deploy-p4.ps1 -Test

# Clean up all containers, volumes, and networks
.\deploy-p4.ps1 -Clean

# Build images (uses pre-built Perforce images)
.\deploy-p4.ps1 -Build
```

## Testing with P4Sync

Once the environment is running, you can test P4Sync with these configurations:

### Source Configuration (for testing sync from this server)
```json
{
  "Name": "TestSource",
  "Source": {
    "Port": "localhost:1666",
    "User": "admin",
    "Workspace": "testworkspace"
  },
  "Target": {
    "Port": "other-server:1666",
    "User": "targetuser",
    "Workspace": "targetworkspace"
  },
  "SyncFilter": ["//depot/src/...", "//depot/docs/..."],
  "Schedule": "0 * * * *"
}
```

### Target Configuration (for testing sync to this server)
```json
{
  "Name": "TestTarget",
  "Source": {
    "Port": "source-server:1666",
    "User": "sourceuser",
    "Workspace": "sourceworkspace"
  },
  "Target": {
    "Port": "localhost:1666",
    "User": "admin",
    "Workspace": "testworkspace"
  },
  "SyncFilter": ["//depot/src/...", "//depot/docs/..."],
  "Schedule": "0 * * * *"
}
```

## Automated Testing

Use the included PowerShell test script to automatically validate P4Sync functionality:

### Run Full Test Suite
```powershell
.\test-p4sync.ps1
```

### Test with Custom Configuration
```powershell
.\test-p4sync.ps1 -ConfigFile "custom-config.json"
```

### Verbose Output
```powershell
.\test-p4sync.ps1 -Verbose
```

### Test Script Features
- **Environment Validation**: Checks Perforce server connectivity and depot contents
- **Binary Testing**: Validates P4Sync executable is built and functional
- **Configuration Validation**: Ensures test configuration is properly formatted
- **Integration Testing**: Runs P4Sync with the test environment and measures performance
- **Detailed Reporting**: Provides clear success/failure indicators with troubleshooting hints

The test script will automatically:
1. Verify the Podman environment is running
2. Check that P4Sync binary exists and executes
3. Validate the configuration file syntax and required fields
4. Run P4Sync against the test environment
5. Report results with timing information

## Sample Test Files

The environment includes sample files for testing:

- `//depot/src/main.cs` - C# source file
- `//depot/src/utils.cs` - C# utility file
- `//depot/docs/readme.txt` - Documentation file
- `//depot/config/settings.json` - Configuration file

## Troubleshooting

### Container Won't Start
1. Check Podman is running: `podman system info`
2. Check for port conflicts: `netstat -an | findstr :1666`
3. Clean and restart: `.\deploy-p4.ps1 -Clean; .\deploy-p4.ps1 -Start`

### Connection Issues
1. Verify containers are running: `podman ps`
2. Check container logs: `podman logs p4sync-test-server`
3. Test manual connection: `podman exec -it p4sync-test-server p4 info`

### Permission Issues
1. Ensure Podman is running as administrator or with proper permissions
2. Check firewall settings for port 1666
3. Verify user credentials match the configured values

## Development Notes

- The environment uses official Perforce Helix Core images
- Data persists in named volumes between container restarts
- Test data is mounted from the `test-data` directory
- Server configuration is automated in the docker-compose.yml

## Cleanup

To completely remove the testing environment:

```powershell
.\deploy-p4.ps1 -Clean
```

This will remove all containers, volumes, and networks created by the deployment.