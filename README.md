# P4Sync

A robust, enterprise-grade .NET 10.0 application for synchronizing files between Perforce servers with advanced filtering, scheduling, and comprehensive testing capabilities.

## Features

- **Flexible Perforce Integration**: Support for both embedded P4 library and external p4.exe operations
- **Advanced File Synchronization**: Sync files between source and target Perforce servers with intelligent conflict handling
- **Sophisticated Filtering**: .gitignore-style pattern matching with include/exclude patterns and bidirectional filtering
- **Automated Scheduling**: Schedule syncs using cron expressions with multiple sync profiles
- **Multi-Profile Support**: Configure multiple sync profiles with different sources, targets, filters, and schedules
- **Comprehensive CLI Interface**: Full command-line interface for all operations with help documentation
- **Configuration Validation**: JSON schema validation for configuration files
- **Enterprise Logging**: Structured logging with configurable levels and output formats
- **Containerized Testing**: Complete Docker/Podman environment for isolated testing
- **Change Management**: Submit all changes in organized changelists with proper metadata
- **Auto-Submit Functionality**: Automatically submit changelists after sync operations
- **Workspace-Aware Path Mapping**: Intelligent path translation that respects Perforce workspace configurations
- **Backward Compatibility**: Maintains compatibility with existing configurations and workflows

## Installation and Setup

### Prerequisites

- **.NET 10.0 SDK** or later
- **Perforce Command Line Tools** (p4.exe) if using external P4 operations
- **Access to Perforce servers** with appropriate user credentials and workspace configurations

### Building from Source

1. **Clone the repository**:
   ```bash
   git clone <repository-url>
   cd P4Sync
   ```

2. **Restore dependencies**:
   ```bash
   dotnet restore src/P4Sync.csproj
   ```

3. **Build the application**:
   ```bash
   dotnet build src/P4Sync.csproj
   ```

4. **Run the application**:
   ```bash
   cd src
   dotnet run
   ```

### Configuration

Before using P4Sync, you need to configure your Perforce server connections and sync profiles in the `config.json` file.

## How to use

1. Follow the [Installation and Setup](#installation-and-setup) instructions above.
2. Update the `config.json` file with your Perforce server details, sync profiles, filters, and schedules.
3. Run the application using `dotnet run` or use the CLI commands below.

## Command Line Interface

P4Sync now supports a command-line interface for easier usage:

### Commands

- `dotnet run sync [--config <file>]` - Execute synchronization based on configuration
- `dotnet run init [--output <file>]` - Create a configuration template
- `dotnet run list-profiles [--config <file>]` - Display available sync profiles
- `dotnet run validate-config [--config <file>]` - Validate configuration file
- `dotnet run --help` - Show help information

### Advanced Usage

#### Running Specific Profiles

While the CLI doesn't currently support running individual profiles, you can create separate configuration files for different environments:

```bash
# Production configuration
dotnet run sync --config production-config.json

# Development configuration  
dotnet run sync --config development-config.json

# Staging configuration
dotnet run sync --config staging-config.json
```

#### Configuration Validation

Always validate your configuration before running sync operations:

```bash
dotnet run validate-config --config myconfig.json
```

This will check for:
- Required fields presence
- JSON schema compliance
- Perforce connection parameter validation
- Path mapping syntax correctness

## Troubleshooting

### Common Issues and Solutions

#### Auto-Submit Failures

**Problem**: Auto-submit fails with "Some file(s) could not be transferred from client"

**Solution**: This usually indicates that file content wasn't written to the local file system before submission. Ensure:
- The target workspace has proper permissions
- There's sufficient disk space
- The client root directory is accessible
- Check the debug logs for specific file path issues

#### Path Mapping Issues

**Problem**: Files aren't syncing to expected locations

**Solutions**:
- Verify workspace client roots are correctly configured
- Check that `p4 fstat` and `p4 where` commands work manually
- Ensure path mappings use correct Perforce depot syntax (`//depot/path/`)
- Review debug logs for path translation details

#### Connection Problems

**Problem**: Unable to connect to Perforce servers

**Solutions**:
- Verify server ports and credentials
- Check network connectivity
- Ensure workspaces exist and are properly configured
- Test connections manually with `p4 info`

#### Filter Pattern Issues

**Problem**: Unexpected files are being synced or filtered out

**Solutions**:
- Use Perforce filespec syntax: `//depot/path/...pattern`
- Test patterns manually with `p4 files` command
- Check for typos in depot paths
- Review filter precedence and combination effects

#### Performance Issues

**Problem**: Sync operations are slow

**Solutions**:
- Use more specific filter patterns to reduce file scanning
- Enable external P4 operations for better performance
- Schedule syncs during off-peak hours
- Consider workspace view optimizations

### Debug Logging

Enable debug logging to troubleshoot issues:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "P4Sync": "Debug"
    }
  }
}
```

### Getting Help

For additional support:
1. Check the debug logs for detailed error information
2. Validate your configuration with `validate-config` command
3. Test Perforce connections manually
4. Review the examples in this documentation

### Examples

```bash
# Create a new configuration template
dotnet run init --output myconfig.json

# List all sync profiles in a configuration
dotnet run list-profiles --config myconfig.json

# Validate a configuration file
dotnet run validate-config --config myconfig.json

# Run synchronization
dotnet run sync --config myconfig.json

# Show help
dotnet run --help
```

### Backward Compatibility

The application maintains backward compatibility. Running `dotnet run` without arguments will use the default `config.json` file, just like before.

## Filter Patterns

The filtering system uses Perforce file specification (filespec) syntax:

- Use Perforce depot paths starting with `//depot/`
- Use `...` to match any number of directories and files recursively
- To filter by file extension, use `...` followed by the extension (e.g., `...cs`, `...txt`)
- Paths specify exact depot locations in Perforce

Example patterns:
- `//depot/main/...` - Include all files in the main branch recursively
- `//depot/main/....cs` - Include all C# files in the main branch
- `//depot/docs/....txt` - Include all text files in documentation
- `//depot/src/....java` - Include all Java files in source directory
- `//depot/config/....json` - Include all JSON files in config directory

## Scheduling

Sync profiles can be scheduled using cron expressions. Some examples:

- `0 * * * *` - Every hour
- `0 */2 * * *` - Every 2 hours
- `0 0 * * *` - Once a day at midnight
- `0 0 * * 1-5` - Weekdays at midnight
- `0 12,18 * * *` - Twice a day at noon and 6 PM

## Configuration

## Workspace-Aware Path Mapping

P4Sync now supports sophisticated workspace-aware path mapping that respects Perforce workspace configurations and client views. This feature allows you to sync files between servers with different depot structures while maintaining proper workspace mappings.

### How Workspace-Aware Path Mapping Works

The workspace-aware path mapping uses a three-step process:

1. **Resolve Depot to Client**: Uses `p4 fstat` to resolve depot paths to their corresponding client file paths
2. **Calculate Relative Path**: Converts client file paths to relative paths using the source workspace's client root
3. **Resolve to Target Depot**: Uses `p4 where` to resolve the relative path back to a depot path on the target server

### Benefits

- **Respects Workspace Views**: Works correctly with complex Perforce workspace view mappings
- **Handles Different Depot Structures**: Maps between servers with different depot hierarchies
- **Automatic Fallback**: Falls back to simple string replacement if workspace information is unavailable
- **Robust Error Handling**: Continues operation even if individual path translations fail

### Configuration Example

```json
{
  "Name": "Workspace-Aware Sync",
  "Source": {
    "Port": "perforce:1666",
    "User": "developer",
    "Workspace": "dev_workspace"
  },
  "Target": {
    "Port": "perforce:1667",
    "User": "builder",
    "Workspace": "build_workspace"
  },
  "SyncFilter": [
    "//depot/source/....cs"
  ],
  "PathMappings": {
    "//depot/source/": "//projects/build/"
  }
}
```

### Path Translation Process

For a file `//depot/source/main/File.cs`:

1. **Source Resolution**: `p4 fstat //depot/source/main/File.cs` → `/home/dev/workspace/main/File.cs`
2. **Relative Path**: Remove source client root `/home/dev/workspace/` → `main/File.cs`
3. **Target Resolution**: `p4 where main/File.cs` on target → `//projects/build/main/File.cs`

### Fallback Behavior

If workspace information cannot be retrieved or path resolution fails, P4Sync automatically falls back to simple string replacement using the `PathMappings` configuration.

### Advanced Examples

#### Multiple Path Mappings
```json
{
  "PathMappings": {
    "//depot/main/src/": "//projects/release/bin/",
    "//depot/main/tests/": "//projects/release/tests/",
    "//depot/docs/": "//projects/docs/"
  }
}
```

#### Cross-Platform Workspace Mapping
```json
{
  "Source": {
    "Port": "perforce:1666",
    "User": "user",
    "Workspace": "unix_workspace"
  },
  "Target": {
    "Port": "perforce:1667",
    "User": "user",
    "Workspace": "windows_workspace"
  },
  "PathMappings": {
    "//depot/unix/": "//depot/windows/"
  }
}
```

This handles the automatic path separator conversion between Unix (`/`) and Windows (`\`) paths.

## Auto-Submit Functionality

P4Sync now supports automatic changelist submission after sync operations, making the entire sync process fully automated without requiring manual intervention.

### How Auto-Submit Works

When `AutoSubmit` is enabled in a sync profile:

1. **File Operations**: Files are added or edited in the target workspace
2. **Content Writing**: File content is written to the local file system
3. **Changelist Management**: All changes are organized into a single changelist
4. **Automatic Submission**: The changelist is automatically submitted with a descriptive message
5. **Error Handling**: Comprehensive error handling with detailed logging

### Configuration

Enable auto-submit by adding `"AutoSubmit": true` to your sync profile:

```json
{
  "Name": "Auto-Submit Sync",
  "Source": {
    "Port": "perforce:1666",
    "User": "developer",
    "Workspace": "dev_workspace"
  },
  "Target": {
    "Port": "perforce:1667",
    "User": "builder",
    "Workspace": "build_workspace"
  },
  "SyncFilter": [
    "//depot/source/....cs"
  ],
  "AutoSubmit": true,
  "Schedule": "0 */2 * * *"
}
```

### Benefits

- **Fully Automated**: No manual changelist submission required
- **Consistent Workflow**: Ensures all sync operations are properly committed
- **Error Prevention**: Reduces risk of forgotten changelist submissions
- **Audit Trail**: Automatic changelist descriptions with timestamps
- **Backward Compatible**: Disabled by default to maintain existing workflows

### Changelist Messages

Auto-submitted changelists include descriptive messages:
- **Add Operations**: `"P4Sync additions YYYY-MM-DD HH:mm:ss"`
- **Edit Operations**: `"P4Sync updates YYYY-MM-DD HH:mm:ss"`
- **Delete Operations**: `"P4Sync deletions YYYY-MM-DD HH:mm:ss"`

### Safety Features

- **Opt-in Only**: Auto-submit must be explicitly enabled per profile
- **Error Handling**: Failed submissions are logged but don't break the sync process
- **Validation**: Pre-submission checks ensure files are properly staged
- **Fallback**: Manual submission still possible if auto-submit fails

### Complete Configuration Example

Here's a comprehensive configuration example showing all available features:

```json
{
  "UseExternalP4": true,
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "P4Sync": "Debug"
    },
    "Console": {
      "FormatterName": "simple",
      "IncludeScopes": false
    }
  },
  "SyncProfiles": [
    {
      "Name": "Production Sync with Auto-Submit",
      "Source": {
        "Port": "prod-perforce.company.com:1666",
        "User": "builduser",
        "Workspace": "prod_workspace"
      },
      "Target": {
        "Port": "staging-perforce.company.com:1666",
        "User": "builduser",
        "Workspace": "staging_workspace"
      },
      "SyncFilter": [
        "//depot/main/src/....cs",
        "//depot/main/config/....json",
        "//depot/main/docs/....md"
      ],
      "PathMappings": {
        "//depot/main/src/": "//projects/staging/src/",
        "//depot/main/config/": "//projects/staging/config/",
        "//depot/main/docs/": "//projects/staging/docs/"
      },
      "AutoSubmit": true,
      "Schedule": "0 */4 * * *",
      "UseExternalP4": true
    },
    {
      "Name": "Development Manual Sync",
      "Source": {
        "Port": "dev-perforce.company.com:1666",
        "User": "developer",
        "Workspace": "dev_workspace"
      },
      "Target": {
        "Port": "dev-perforce.company.com:1666",
        "User": "developer",
        "Workspace": "test_workspace"
      },
      "SyncFilter": [
        "//depot/dev/....cs",
        "//depot/dev/....txt"
      ],
      "AutoSubmit": false,
      "UseExternalP4": false
    }
  ]
}
```

This example demonstrates:
- **Global Settings**: `UseExternalP4` and logging configuration
- **Auto-Submit**: Enabled for production, disabled for development
- **Path Mappings**: Custom workspace-aware path translation
- **Profile Overrides**: `UseExternalP4` can be overridden per profile
- **Scheduling**: Different schedules for different environments
- **Filtering**: Multiple file patterns for different content types

-   `SyncProfiles`: An array of sync profiles with the following properties:
    -   `Name`: A unique name for the profile.
    -   `Source`: The source Perforce server details for this profile.
    -   `Target`: The target Perforce server details for this profile.
    -   `SyncFilter`: Array of Perforce filespec patterns for filtering files during synchronization (e.g., "//depot/main/...cs" for C# files).
    -   `Schedule`: Cron expression for scheduling the sync (e.g., "0 */2 * * *" for every 2 hours).
    -   `AutoSubmit`: (Optional) Automatically submit changelists after sync operations (default: false).
    -   `PathMappings`: (Optional) Dictionary of path mappings for workspace-aware translation.
    -   `UseExternalP4`: (Optional) Override global UseExternalP4 setting for this profile.

## Automated Testing

P4Sync includes comprehensive automated testing infrastructure using Podman containers for isolated Perforce server testing.

### Setting up Test Environment

1. **Prerequisites**: Install Podman and ensure it's running
2. **Deploy Dual-Server Environment**:
   ```bash
   cd deploy
   .\deploy-p4.ps1 -Start
   ```
   This creates two Perforce servers:
   - **Source Server**: `localhost:1666` (with test data)
   - **Target Server**: `localhost:1667` (initially empty)

3. **Run Connection Tests**:
   ```bash
   .\deploy-p4.ps1 -Test
   ```

4. **Run End-to-End Sync Test**:
   ```bash
   .\deploy-p4.ps1 -EndToEndTest
   ```
   This comprehensive test:
   - Populates test data on the source server
   - Runs P4Sync to sync from source to target
   - Verifies that files are correctly filtered and synced

### Test Scripts

- `deploy/deploy-p4.ps1` - Dual-server Podman environment management
- `deploy/test-end-to-end.ps1` - Comprehensive end-to-end sync validation
- `deploy/docker-compose.yml` - Dual-server container orchestration
- `src/test-config.json` - Test configuration for automated testing

### Test Data Structure

The test environment creates the following files on the source server:

```
test-files/
├── TestClass.cs     (✅ Should sync - matches *.cs filter)
├── readme.txt       (✅ Should sync - matches *.txt filter)
├── data.bin         (❌ Should NOT sync)
├── obj/temp.obj     (❌ Should NOT sync)
└── bin/app.exe      (❌ Should NOT sync)
```

The end-to-end test verifies that only `TestClass.cs` and `readme.txt` appear on the target server.

### Running Unit Tests

```bash
dotnet test tests/P4Sync.Tests.csproj
```

### Test Coverage

- **Unit Tests**: Test individual components and methods
- **Integration Tests**: Test with real Perforce server connections
- **Contract Tests**: Validate configuration schema compliance
- **CLI Tests**: Test command-line interface functionality

The testing environment uses the `waisbrot/perforce` Docker image to provide a realistic Perforce server for validation without requiring a full Perforce installation.
