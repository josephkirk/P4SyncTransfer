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

The `config.json` file has the following structure:

```json
{
  "SyncProfiles": [
    {
      "Name": "MainProfile",
      "Source": {
        "Port": "perforce:1666",
        "User": "user",
        "Workspace": "workspace"
      },
      "Target": {
        "Port": "perforce:1667",
        "User": "user",
        "Workspace": "workspace"
      },
      "SyncFilter": ["//depot/main/....cs", "//depot/docs/....md"],
      "Schedule": "0 */2 * * *"
    },
    {
      "Name": "SecondaryProfile",
      "Source": {
        "Port": "perforce:1666",
        "User": "user2",
        "Workspace": "workspace2"
      },
      "Target": {
        "Port": "perforce:1667",
        "User": "user2",
        "Workspace": "workspace2"
      },
      "SyncFilter": ["//depot/binaries/...dll", "//depot/binaries/...exe"],
      "Schedule": "0 0 * * *"
    }
  ]
}
```

-   `SyncProfiles`: An array of sync profiles with the following properties:
    -   `Name`: A unique name for the profile.
    -   `Source`: The source Perforce server details for this profile.
    -   `Target`: The target Perforce server details for this profile.
    -   `SyncFilter`: Array of Perforce filespec patterns for filtering files during synchronization (e.g., "//depot/main/...cs" for C# files).
    -   `Schedule`: Cron expression for scheduling the sync (e.g., "0 */2 * * *" for every 2 hours).

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
