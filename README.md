# P4 To P4 Sync Transfer

A .NET application for syncing files between Perforce servers. The purpose is to help manage file transfers in my development workflow, with features like filtering, scheduling, and automated submissions.

## What It Does

- Syncs files between Perforce servers with intelligent filtering using Perforce filespec patterns
- Supports multiple sync profiles and cron-based scheduling
- Uses SHA256 hashing to compare file contents
- Can run automatically with cron-like scheduling
- Handles path mappings between different workspaces
- Auto-submits changes to keep things tidy
- Tracks sync history in JSON files (stored in `logs/history/`)
- Logs operations to daily files (in `logs/app_YYYY-MM-DD.log` format)
- Works with embedded P4 library or external p4.exe

## Getting Started

### Requirements

- .NET 9.0 SDK
- Access to Perforce servers (p4.exe if using external mode)

### Setup

1. Clone this repo:
   ```bash
   git clone <your-repo-url>
   cd P4SyncTransfer
   ```

2. Build it:
   ```bash
   dotnet build src
   ```

3. Configure your settings in `config.json` (see examples in the config files).

4. Run it:
   ```bash
   cd src
   dotnet run sync
   ```

## Usage

- **Sync files**: `dotnet run sync --config your-config.json`
- **Create config**: `dotnet run init --output config.json`
- **Validate config**: `dotnet run validate-config --config config.json`
- **List profiles**: `dotnet run list-profiles --config config.json`

Check `dotnet run --help` for more options.

## Configuration

Edit `config.json` to set up your Perforce servers, filters, and schedules. There are example configs in the `src/` folder.

Key parts:
- `SyncProfiles`: List of sync jobs
- `SyncFilter`: Perforce filespec patterns like `//depot/main/....cs` for C# files
- `Schedule`: Cron expressions for timing
- `AutoSubmit`: Set to true for automatic changelist submission
- `UseExternalP4`: Choose between embedded P4 library (false) or external p4.exe (true)

## Sync History

The application tracks detailed sync history in JSON format to help with auditing, debugging, and operation tracking:

- **Location**: Stored in `logs/history/` directory with daily files
- **Contents**: Each sync operation records source/target paths, file operations (add/edit/delete), revisions, content hashes, and success/failure status
- **Usage**: 
  - **Prevents redundant transfers**: Avoids syncing files that have already been processed at the head revision
  - Audit what files were synced and when
  - Debug sync issues by reviewing operation details
  - Track changelist numbers for submitted changes
  - Monitor sync success/failure rates

## Testing

Run tests with:
```bash
dotnet test tests/P4Sync.Tests.csproj
```

For full end-to-end testing with p4 servers hosted with podman containers:
```bash
./tests/deploy/test-end-to-end.ps1
```

## Why I Made This

I needed a reliable way to sync code between different Perforce environments without manual hassle. 

Feel free to use it or modify it for your own needs!
