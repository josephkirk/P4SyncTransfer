# P4 To P4 Sync Transfer

A .NET application for syncing files between Perforce servers. The purpose is to help manage file transfers in my development workflow, with features like filtering, scheduling, and automated submissions.

## What It Does

- Syncs files between Perforce servers with intelligent filtering using Perforce filespec patterns
- Supports multiple sync profiles and cron-based scheduling
- Uses SHA256 hashing to compare file contents
- Can run automatically with cron-like scheduling
- Handles path mappings between different workspaces
- Auto-submits changes to keep things tidy
- Tracks sync history in JSON files (stored in `logs/history/` or custom directory)
- Logs operations to daily files (in `logs/app_YYYY-MM-DD.log` format or custom directory)
- Query sync history with filtering and detailed transfer information
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

- **Sync files**: `P4Sync sync --config your-config.json [--logs <directory>]`
- **Create config**: `P4Sync init --output config.json`
- **Validate config**: `P4Sync validate-config --config config.json`
- **List profiles**: `P4Sync list-profiles --config config.json`
- **Query history**: `P4Sync query-history [--logs <directory>] [--profile <name>] [--date <yyyy-MM-dd>] [--limit <n>] [--transfers]`

Check `P4Sync --help` for more options.

## Configuration

Edit `config.json` to set up your Perforce servers, filters, and schedules. There are example configs in the `src/` folder.

Key parts:
- `SyncProfiles`: List of sync jobs
- `SyncFilter`: Perforce filespec patterns like `//depot/main/....cs` for C# files
- `Schedule`: Cron expressions for timing
- `AutoSubmit`: Set to true for automatic changelist submission

### Log Directory

By default, logs and history are stored in `./logs` relative to the current working directory. You can specify a custom log directory using the `--logs <directory>` option with sync and query-history commands.

## Sync History

The application tracks detailed sync history in JSON format to help with auditing, debugging, and operation tracking:

- **Location**: Stored in `logs/history/` directory with daily files (or custom directory via `--logs`)
- **Contents**: Each sync operation records source/target paths, file operations (add/edit/delete), revisions, content hashes, and success/failure status
- **Querying History**: Use the `query-history` command to view and filter sync history:
  - `--logs <directory>`: Specify custom log directory (default: `./logs`)
  - `--profile <name>`: Filter by sync profile name
  - `--date <yyyy-MM-dd>`: Show history for specific date
  - `--limit <n>`: Limit number of results (default: 10)
  - `--transfers`: Show detailed file transfer information

- **Usage**:
  - **Prevents redundant transfers**: Avoids syncing files that have already been processed at the head revision
  - Audit what files were synced and when
  - Debug sync issues by reviewing operation details
  - Track changelist numbers for submitted changes
  - Monitor sync success/failure rates

### Examples

```bash
# View recent sync history
P4Sync query-history

# View history for specific profile
P4Sync query-history --profile "My Profile"

# View history for today with transfer details
P4Sync query-history --date 2025-09-25 --transfers

# View history from custom log directory
P4Sync query-history --logs /var/log/p4sync --limit 5
```

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
