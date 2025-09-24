using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;
using Microsoft.Extensions.Logging;
using Perforce.P4;

namespace P4Sync
{
    /// <summary>
    /// Service class that encapsulates all Perforce-related operations using external p4.exe process calls
    /// </summary>
    public class P4OperationExternal : IP4Operations
    {
        private readonly ILogger<P4OperationExternal> _logger;
        private readonly P4SyncHistory _syncHistory;

        /// <summary>
        /// Constructor with dependency injection
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="syncHistory">Sync history instance</param>
        public P4OperationExternal(ILogger<P4OperationExternal> logger, P4SyncHistory syncHistory)
        {
            _logger = logger;
            _syncHistory = syncHistory;
        }

        /// <summary>
        /// Executes synchronization from source to target repository based on profile configuration
        /// </summary>
        /// <param name="profile">Sync profile configuration containing source, target, and filter information</param>
        public void ExecuteSync(SyncProfile profile)
        {
            _logger.LogDebug("P4OperationExternal.ExecuteSync started for profile: {ProfileName}", profile.Name);

            if (profile.Source == null || profile.Target == null)
            {
                _logger.LogError("Profile '{ProfileName}' must have both Source and Target configurations", profile.Name);
                return;
            }

            _logger.LogDebug("Source config: Port={SourcePort}, User={SourceUser}, Workspace={SourceWorkspace}",
                profile.Source.Port, profile.Source.User, profile.Source.Workspace);
            _logger.LogDebug("Target config: Port={TargetPort}, User={TargetUser}, Workspace={TargetWorkspace}",
                profile.Target.Port, profile.Target.User, profile.Target.Workspace);
            _logger.LogDebug("SyncFilter: {SyncFilter}", string.Join(", ", profile.SyncFilter ?? new List<string>()));

            _logger.LogInformation("Executing sync for profile: {ProfileName}", profile.Name);

            // Validate connections (external implementation - just log connection info)
            _logger.LogDebug("Validating source connection");
            if (profile.Source == null)
            {
                _logger.LogError("Source connection is null");
                return;
            }
            _logger.LogDebug("Source connection validated");

            _logger.LogDebug("Validating target connection");
            if (profile.Target == null)
            {
                _logger.LogError("Target connection is null");
                return;
            }
            _logger.LogDebug("Target connection validated");

            // Get client information for path resolution (external implementation)
            _logger.LogDebug("Getting client information");
            // For external implementation, we don't need actual Client objects, just validate workspaces exist
            if (!string.IsNullOrEmpty(profile.Source.Workspace))
            {
                _logger.LogDebug("Source workspace: {Workspace}", profile.Source.Workspace);
            }
            if (!string.IsNullOrEmpty(profile.Target.Workspace))
            {
                _logger.LogDebug("Target workspace: {Workspace}", profile.Target.Workspace);
            }

            // Execute source to target sync
            if (profile.SyncFilter != null && profile.SyncFilter.Any())
            {
                _logger.LogDebug("Executing directional sync with {FilterCount} filter patterns", profile.SyncFilter.Count);
                ExecuteDirectionalSync(profile.Source, profile.Target, profile, profile.SyncFilter, "Source to Target", true);
            }
            else
            {
                _logger.LogWarning("Profile '{ProfileName}' has no filters configured. Skipping sync", profile.Name);
            }
        }

        /// <summary>
        /// Executes directional synchronization from source to target using p4.exe commands
        /// </summary>
        private void ExecuteDirectionalSync(P4Connection source, P4Connection target, SyncProfile profile,
            List<string> filterPatterns, string direction, bool isSourceToTarget)
        {
            try
            {
                var syncTransfersRecord = new P4SyncedTransfers();

                _logger.LogDebug("ExecuteDirectionalSync started");
                _logger.LogInformation("Executing {Direction} sync for profile: {ProfileName}", direction, profile.Name);

                // Create changelist for the sync
                _logger.LogDebug("Creating changelist");
                int changelistId = 0;
                try
                {
                    changelistId = CreateChangelist(target, profile);
                    _logger.LogDebug("Changelist created: {ChangelistId}", changelistId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to create changelist");
                    _logger.LogDebug("Continuing without changelist - using default changelist");
                    changelistId = 0;
                }

                // We get all files we need to process using GetFilteredFiles with the filter patterns
                _logger.LogDebug("Getting source files");
                var sourceFileList = GetFilteredFilesExternal(source, filterPatterns);
                var sourceFiles = sourceFileList.ToDictionary(f => f.DepotPath, f => f);
                _logger.LogDebug("Found {SourceFileCount} source files", sourceFiles.Count);

                // Get corresponding files from target (with path translation)
                _logger.LogDebug("Getting target files for comparison");
                var targetFilterPatterns = filterPatterns.Select(p => p.Replace("//depot/", "//project/")).ToList();
                var targetFileList = GetFilteredFilesExternal(target, targetFilterPatterns);
                var targetFiles = targetFileList.ToDictionary(f => f.DepotPath, f => f);
                _logger.LogDebug("Found {TargetFileCount} target files", targetFiles.Count);

                // Determine sync operations based on file actions and differences
                var operations = DetermineSyncOperations(source, target, sourceFiles, targetFiles, profile);
                _logger.LogDebug("Determined {OperationCount} sync operations", operations.Count);

                // Initialize sync transfer record with proper data
                syncTransfersRecord.SyncTime = DateTime.Now;
                syncTransfersRecord.Transfers = new List<P4SyncedTransfer>();

                // Execute sync operations
                foreach (var operation in operations)
                {
                    var sourceDepotPath = operation.Key;
                    var syncOperation = operation.Value;
                    
                    _logger.LogDebug("Processing operation: {SourcePath} -> {Operation}", sourceDepotPath, syncOperation);

                    // Get file information
                    P4FileInfo? sourceFileInfo = null;
                    P4FileInfo? targetFileInfo = null;
                    string targetDepotPath;

                    if (sourceFiles.ContainsKey(sourceDepotPath))
                    {
                        sourceFileInfo = sourceFiles[sourceDepotPath];
                        targetDepotPath = TranslateSourcePathToTarget(sourceDepotPath, profile);
                        if (targetFiles.ContainsKey(targetDepotPath))
                        {
                            targetFileInfo = targetFiles[targetDepotPath];
                        }
                    }
                    else
                    {
                        // This shouldn't happen with the new logic, but handle it just in case
                        targetDepotPath = sourceDepotPath;
                        if (targetFiles.ContainsKey(targetDepotPath))
                        {
                            targetFileInfo = targetFiles[targetDepotPath];
                        }
                    }

                    // Create sync transfer record
                    var syncTransferRecord = new P4SyncedTransfer
                    {
                        SourceDepotPath = sourceDepotPath,
                        SourceLocalPath = ResolveDepotPathToClientFile(source, sourceDepotPath),
                        TargetDepotPath = targetDepotPath,
                        TargetLocalPath = ResolveDepotPathToClientFile(target, targetDepotPath),
                        SourceRevision = sourceFileInfo?.Revision ?? 0,
                        TargetRevision = targetFileInfo?.Revision ?? 0,
                        SourceAction = sourceFileInfo?.Action ?? "unknown",
                        TargetOperation = syncOperation,
                        ContentHash = string.Empty, // Not available in external mode
                    };

                    // Apply the operation
                    bool success = ApplyP4ActionToTargetExternal(source, target, sourceDepotPath, targetDepotPath, syncOperation, changelistId, profile, targetFileInfo != null);

                    if (!success)
                    {
                        syncTransferRecord.ErrorMessage = $"Failed to apply action {syncOperation} for file {sourceDepotPath} to target {targetDepotPath}";
                        _logger.LogDebug("Failed to apply action {Action} for file {SourcePath} to target {TargetPath}", syncOperation, sourceDepotPath, targetDepotPath);
                    }
                    syncTransferRecord.Success = success;
                    syncTransfersRecord.Transfers.Add(syncTransferRecord);
                }
                
                syncTransfersRecord.ChangelistNumber = changelistId;
                
                // Submit the changelist if any files were modified and auto-submit is enabled
                if (changelistId > 0 && profile.AutoSubmit)
                {
                    SubmitOrDeleteChangelistExternal(target, changelistId, syncTransfersRecord.Transfers.Count != 0);
                }
                else
                {
                    if (!profile.AutoSubmit)
                    {
                        _logger.LogDebug("Auto-submit disabled for profile {ProfileName}, changelist left pending", profile.Name);
                    }
                    else
                    {
                        _logger.LogDebug("Cannot submit changelist - changelist not created or auto-submit disabled");
                    }
                }

                // Log sync operations summary
                _logger.LogInformation("sync completed.");
                

                // Log sync history
                if (syncTransfersRecord.Transfers.Count > 0)
                {
                    _logger.LogInformation("sync for profile {ProfileName} completed.", profile.Name);
                    var syncHistory = new SyncHistory
                    {
                        Profile = profile,
                        Syncs = new List<P4SyncedTransfers> { syncTransfersRecord }
                    };
                    _syncHistory.LogSync(syncHistory);
                    _logger.LogDebug("Sync history logged for profile {ProfileName}", profile.Name);
                }
                
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("p4.exe") || ex.Message.Contains("Failed to start"))
            {
                _logger.LogWarning(ex, "p4.exe not available or not found in PATH. External P4 operations cannot be performed. Consider using embedded P4 library by setting UseExternalP4=false in configuration.");
                _logger.LogInformation("{Direction} sync for profile {ProfileName} skipped due to missing p4.exe", direction, profile.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing {Direction} sync for profile {ProfileName}", direction, profile.Name);
            }
        }

        /// <summary>
        /// Creates a changelist on the target server using external p4 commands
        /// </summary>
        private int CreateChangelist(P4Connection target, SyncProfile profile)
        {
            try
            {
                var description = GetChangelistDescription(profile);
                var args = new List<string> { "changelist", "-i" };
                var input = $"Change: new\n\nDescription:\n\t{description.Replace("\n", "\n\t")}\n\n";

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "p4.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                };

                // Set environment variables
                processStartInfo.EnvironmentVariables["P4PORT"] = target.Port;
                processStartInfo.EnvironmentVariables["P4USER"] = target.User;
                processStartInfo.EnvironmentVariables["P4CLIENT"] = target.Workspace;

                foreach (var arg in args)
                {
                    processStartInfo.ArgumentList.Add(arg);
                }

                using var process = Process.Start(processStartInfo);
                if (process == null)
                {
                    throw new InvalidOperationException("Failed to start p4.exe process");
                }

                // Write the changelist spec to stdin
                using (var writer = process.StandardInput)
                {
                    writer.Write(input);
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();

                process.WaitForExit();

                if (process.ExitCode == 0)
                {
                    // Parse the changelist number from output
                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("Change "))
                        {
                            var parts = line.Split(' ');
                            if (parts.Length >= 2 && int.TryParse(parts[1], out var changeId))
                            {
                                return changeId;
                            }
                        }
                    }
                }

                throw new InvalidOperationException($"Failed to create changelist: {error}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating changelist");
                throw;
            }
        }
        private string GetChangelistDescription(SyncProfile profile)
        {
            if (!string.IsNullOrEmpty(profile.Description))
            {
                // Use custom description with keyword replacement
                return ReplaceDescriptionKeywords(profile.Description, profile);
            }
            else
            {
                // Use default description
                return ReplaceDescriptionKeywords("[Auto] P4 Sync Transfer from {source_server} {source_workspace}", profile);
            }
        }

        /// <summary>
        /// Replaces keywords in changelist description
        /// </summary>
        private string ReplaceDescriptionKeywords(string description, SyncProfile profile)
        {
            return description
                .Replace("{source_server}", profile.Source?.Port ?? "unknown")
                .Replace("{source_workspace}", profile.Source?.Workspace ?? "unknown")
                .Replace("{target_server}", profile.Target?.Port ?? "unknown")
                .Replace("{target_workspace}", profile.Target?.Workspace ?? "unknown")
                .Replace("{profile_name}", profile.Name ?? "unknown")
                .Replace("{now}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        /// <summary>
        /// Simple structure to hold workspace client information
        /// </summary>
        private class WorkspaceInfo
        {
            public string ClientRoot { get; set; } = string.Empty;
            public Dictionary<string, string> ViewMappings { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, string> DepotToClientMappings { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, string> ClientToDepotMappings { get; set; } = new Dictionary<string, string>();
        }

        /// <summary>
        /// Simple structure to hold P4 file information
        /// </summary>
        private class P4FileInfo
        {
            public string DepotPath { get; set; } = string.Empty;
            public int Revision { get; set; }
            public string Action { get; set; } = string.Empty;
        }

        /// <summary>
        /// Gets workspace client information including root and view mappings
        /// </summary>
        private WorkspaceInfo GetWorkspaceInfo(P4Connection connection)
        {
            var workspaceInfo = new WorkspaceInfo();

            try
            {
                var args = new List<string> { "client", "-o", connection.Workspace ?? "" };
                var output = ExecuteP4Command(connection, args, setWorkingDirectory: false);

                if (string.IsNullOrEmpty(output))
                {
                    _logger.LogWarning("Could not get workspace info for {Workspace}", connection.Workspace);
                    return workspaceInfo;
                }

                var lines = output.Split('\n');
                var inViewSection = false;

                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();

                    if (trimmedLine.StartsWith("Root:"))
                    {
                        workspaceInfo.ClientRoot = trimmedLine.Substring(5).Trim();
                        _logger.LogDebug("Found client root: {Root}", workspaceInfo.ClientRoot);
                    }
                    else if (trimmedLine.StartsWith("View:"))
                    {
                        inViewSection = true;
                    }
                    else if (inViewSection && trimmedLine.StartsWith("\t"))
                    {
                        // Parse view mapping: "\t//depot/path/... //client/path/..."
                        var mapping = trimmedLine.Trim();
                        var parts = mapping.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            var depotPath = parts[0];
                            var clientPath = parts[1];

                            workspaceInfo.ViewMappings[depotPath] = clientPath;
                            workspaceInfo.DepotToClientMappings[depotPath] = clientPath;
                            workspaceInfo.ClientToDepotMappings[clientPath] = depotPath;

                            _logger.LogDebug("Parsed view mapping: {DepotPath} -> {ClientPath}", depotPath, clientPath);
                        }
                    }
                    else if (inViewSection && string.IsNullOrWhiteSpace(trimmedLine))
                    {
                        // End of view section
                        break;
                    }
                }

                _logger.LogDebug("Retrieved workspace info for {Workspace}: Root={Root}, ViewMappings={MappingCount}",
                    connection.Workspace, workspaceInfo.ClientRoot, workspaceInfo.ViewMappings.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting workspace info for {Workspace}", connection.Workspace);
            }

            return workspaceInfo;
        }


        /// <summary>
        /// Discovers path mappings based on stream relationships
        /// </summary>
        private Dictionary<string, string> DiscoverStreamBasedMappings(P4Connection source, P4Connection target)
        {
            var streamMappings = new Dictionary<string, string>();

            try
            {
                // Get stream information from both workspaces
                var sourceStream = GetWorkspaceStream(source);
                var targetStream = GetWorkspaceStream(target);

                if (string.IsNullOrEmpty(sourceStream) || string.IsNullOrEmpty(targetStream))
                {
                    _logger.LogDebug("Could not retrieve stream information for one or both workspaces");
                    return streamMappings;
                }

                _logger.LogDebug("Source workspace uses stream: {SourceStream}", sourceStream);
                _logger.LogDebug("Target workspace uses stream: {TargetStream}", targetStream);

                // Check if streams are related (same depot but different branches)
                if (AreStreamsRelated(sourceStream, targetStream))
                {
                    // Create mapping from source stream to target stream
                    var sourceStreamBase = GetStreamBasePath(sourceStream);
                    var targetStreamBase = GetStreamBasePath(targetStream);

                    if (!string.IsNullOrEmpty(sourceStreamBase) && !string.IsNullOrEmpty(targetStreamBase))
                    {
                        streamMappings[sourceStreamBase] = targetStreamBase;
                        _logger.LogDebug("Created stream-based mapping: {Source} -> {Target}",
                            sourceStreamBase, targetStreamBase);
                    }
                }
                else
                {
                    _logger.LogDebug("Streams {SourceStream} and {TargetStream} are not related for automatic mapping",
                        sourceStream, targetStream);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during stream-based mapping discovery");
            }

            return streamMappings;
        }

        /// <summary>
        /// Gets the stream used by a workspace
        /// </summary>
        private string GetWorkspaceStream(P4Connection connection)
        {
            try
            {
                var args = new List<string> { "client", "-o", connection.Workspace ?? "" };
                var output = ExecuteP4Command(connection, args, setWorkingDirectory: false);

                if (string.IsNullOrEmpty(output))
                {
                    return string.Empty;
                }

                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("Stream:"))
                    {
                        var stream = line.Trim().Substring(7).Trim();
                        return stream;
                    }
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting stream for workspace {Workspace}", connection.Workspace);
                return string.Empty;
            }
        }

        /// <summary>
        /// Checks if two streams are related (same depot, different branches)
        /// </summary>
        private bool AreStreamsRelated(string sourceStream, string targetStream)
        {
            try
            {
                // For now, check if they have the same depot prefix but different names
                // This is a simple heuristic - in a more sophisticated implementation,
                // we could query the stream depot relationship

                var sourceParts = sourceStream.TrimStart('/').Split('/');
                var targetParts = targetStream.TrimStart('/').Split('/');

                if (sourceParts.Length >= 2 && targetParts.Length >= 2)
                {
                    // Check if depot paths match (e.g., //InternalProjects/...)
                    var sourceDepot = $"//{sourceParts[0]}/";
                    var targetDepot = $"//{targetParts[0]}/";

                    if (sourceDepot == targetDepot)
                    {
                        // Same depot, different streams - likely related
                        _logger.LogDebug("Streams {Source} and {Target} appear to be related (same depot)",
                            sourceStream, targetStream);
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking if streams are related");
                return false;
            }
        }

        /// <summary>
        /// Gets the base path for a stream (e.g., //InternalProjects/SPD2/... for stream //InternalProjects/SPD2)
        /// </summary>
        private string GetStreamBasePath(string stream)
        {
            if (string.IsNullOrEmpty(stream))
            {
                return string.Empty;
            }

            // For a stream like //InternalProjects/SPD2, the base path is //InternalProjects/SPD2/...
            return stream + "/";
        }

        /// <summary>
        /// Gets filtered files using p4 fstat command for efficient filtering and existence checking
        /// </summary>
        private List<P4FileInfo> GetFilteredFilesExternal(P4Connection connection, List<string> filterPatterns)
        {
            var filteredFiles = new List<P4FileInfo>();

            try
            {
                // Use p4 fstat for efficient filtering and existence checking
                // This avoids getting all depot files and then filtering client-side
                var fstatArgs = new List<string> { "fstat" };
                fstatArgs.AddRange(filterPatterns);

                _logger.LogDebug("Executing p4 fstat with patterns: {patterns}", string.Join(" ", filterPatterns));

                var (output, success) = ExecuteP4CommandWithStatus(connection, fstatArgs);

                if (!success)
                {
                    _logger.LogWarning("p4 fstat command failed, falling back to files command");
                    // Fallback to the old method if fstat fails
                    return GetFilteredFilesFallback(connection, filterPatterns);
                }

                // Parse fstat output
                var files = ParseP4FstatOutput(output);
                filteredFiles.AddRange(files);

                _logger.LogDebug("Retrieved {FileCount} files matching filter patterns using fstat", filteredFiles.Count);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("p4.exe") || ex.Message.Contains("Failed to start"))
            {
                _logger.LogWarning(ex, "p4.exe not available for getting filtered files. Returning empty list.");
                // Return empty list instead of throwing to allow graceful degradation
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting filtered files with fstat");
                // Try fallback method
                try
                {
                    _logger.LogDebug("Attempting fallback method for getting filtered files");
                    filteredFiles = GetFilteredFilesFallback(connection, filterPatterns);
                }
                catch (Exception fallbackEx)
                {
                    _logger.LogError(fallbackEx, "Fallback method also failed");
                }
            }

            return filteredFiles;
        }

        /// <summary>
        /// Fallback method using p4 files command (less efficient but more compatible)
        /// </summary>
        private List<P4FileInfo> GetFilteredFilesFallback(P4Connection connection, List<string> filterPatterns)
        {
            var filteredFiles = new List<P4FileInfo>();

            try
            {
                // Use p4 files with filter patterns
                var args = new List<string> { "files" };
                args.AddRange(filterPatterns);

                var output = ExecuteP4Command(connection, args);
                var allDepotFiles = ParseP4FilesOutput(output);

                _logger.LogDebug("Retrieved {FileCount} total depot files using files command", allDepotFiles.Count);
                filteredFiles.AddRange(allDepotFiles);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in fallback method for getting filtered files");
            }

            return filteredFiles;
        }

        /// <summary>
        /// Parses the output of 'p4 fstat' command into FileInfo objects without filtering
        /// </summary>
        private List<P4FileInfo> ParseP4FstatOutputNoFilter(string output)
        {
            var files = new List<P4FileInfo>();
            var currentFile = new Dictionary<string, string>();

            if (string.IsNullOrEmpty(output))
            {
                return files;
            }

            // Don't remove empty entries as they are used to separate file records
            var lines = output.Split('\n');

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    // End of current file record
                    if (currentFile.Any())
                    {
                        var fileInfo = CreateFileInfoFromFstat(currentFile);
                        if (fileInfo != null)
                        {
                            files.Add(fileInfo);
                        }
                        currentFile.Clear();
                    }
                    continue;
                }

                // Parse fstat output format: "... key value"
                var trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("... "))
                {
                    var keyValuePart = trimmedLine.Substring(4); // Remove "... "
                    
                    // Split on first space to separate key from value
                    var spaceIndex = keyValuePart.IndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        var key = keyValuePart.Substring(0, spaceIndex);
                        var value = keyValuePart.Substring(spaceIndex + 1).Trim();
                        currentFile[key] = value;
                    }
                    else
                    {
                        // Some fields like "isMapped" have no value
                        currentFile[keyValuePart] = "";
                    }
                }
            }

            // Handle the last file record
            if (currentFile.Any())
            {
                var fileInfo = CreateFileInfoFromFstat(currentFile);
                if (fileInfo != null)
                {
                    files.Add(fileInfo);
                }
            }

            return files;
        }

        /// <summary>
        /// Parses the output of 'p4 fstat' command into FileInfo objects
        /// </summary>
        private List<P4FileInfo> ParseP4FstatOutput(string output)
        {
            var files = new List<P4FileInfo>();
            var currentFile = new Dictionary<string, string>();

            if (string.IsNullOrEmpty(output))
            {
                return files;
            }

            // Don't remove empty entries as they are used to separate file records
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.None);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) && currentFile.Any())
                {
                    var fileInfo = CreateFileInfoFromFstat(currentFile);
                    if (fileInfo != null)
                    {
                        files.Add(fileInfo);
                    }
                    currentFile.Clear();
                }
                else if (line.Trim().StartsWith("... "))
                {
                    var keyValuePart = line.Trim().Substring(4); // Remove "... "
                    
                    // Split on first space to separate key from value
                    var spaceIndex = keyValuePart.IndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        var key = keyValuePart.Substring(0, spaceIndex);
                        var value = keyValuePart.Substring(spaceIndex + 1).Trim();
                        currentFile[key] = value;
                    }
                    else
                    {
                        // Some fields like "isMapped" have no value
                        currentFile[keyValuePart] = "";
                    }
                }
            }

            if (currentFile.Any())
            {
                var fileInfo = CreateFileInfoFromFstat(currentFile);
                if (fileInfo != null)
                {
                    files.Add(fileInfo);
                }
            }

            return files;
        }

        /// <summary>
        /// Creates a FileInfo object from parsed fstat data
        /// </summary>
        private P4FileInfo? CreateFileInfoFromFstat(Dictionary<string, string> fstatData)
        {
            if (!fstatData.ContainsKey("depotFile"))
            {
                return null;
            }

            var depotPath = fstatData["depotFile"];

            // Get revision number
            var revision = 1;
            if (fstatData.ContainsKey("headRev"))
            {
                int.TryParse(fstatData["headRev"], out revision);
            }

            // Get action (headAction takes precedence over action)
            var action = string.Empty;
            if (fstatData.ContainsKey("headAction"))
            {
                action = fstatData["headAction"];
            }
            else if (fstatData.ContainsKey("action"))
            {
                action = fstatData["action"];
            }

            return new P4FileInfo
            {
                DepotPath = depotPath,
                Revision = revision,
                Action = action
            };
        }

        /// <summary>
        /// Determines what sync operations are needed by comparing source and target files with path translation
        /// </summary>
        private Dictionary<string, SyncOperation> DetermineSyncOperations(
            P4Connection source, P4Connection target,
            Dictionary<string, P4FileInfo> sourceFiles,
            Dictionary<string, P4FileInfo> targetFiles,
            SyncProfile profile)
        {
            var operations = new Dictionary<string, SyncOperation>();

            // Handle files from source depot based on their actions
            foreach (var sourceFile in sourceFiles)
            {
                var targetPath = TranslateSourcePathToTarget(sourceFile.Key, profile);
                var action = sourceFile.Value.Action.ToLowerInvariant();
                
                _logger.LogDebug("Processing source file {FilePath} with action '{Action}'", sourceFile.Key, action);

                switch (action)
                {
                    case "add":
                    case "move/add":
                        if (!targetFiles.ContainsKey(targetPath))
                        {
                            operations[sourceFile.Key] = SyncOperation.Add;
                        }
                        else
                        {
                            // File exists in both - check if it needs updating
                            var sourceRev = sourceFile.Value.Revision;
                            var targetRev = targetFiles[targetPath].Revision;
                            
                            if (sourceRev > targetRev)
                            {
                                operations[sourceFile.Key] = SyncOperation.Edit;
                            }
                        }
                        break;

                    case "edit":
                    case "integrate":
                        if (targetFiles.ContainsKey(targetPath))
                        {
                            var sourceRev = sourceFile.Value.Revision;
                            var targetRev = targetFiles[targetPath].Revision;
                            
                            if (sourceRev > targetRev)
                            {
                                operations[sourceFile.Key] = SyncOperation.Edit;
                            }
                        }
                        else
                        {
                            // File doesn't exist on target, treat as add
                            operations[sourceFile.Key] = SyncOperation.Add;
                        }
                        break;

                    case "delete":
                    case "move/delete":
                        // For delete operations, we need to check if the target file exists
                        if (targetFiles.ContainsKey(targetPath))
                        {
                            operations[sourceFile.Key] = SyncOperation.Delete;
                        }
                        break;

                    default:
                        // For files without a specific action or unknown actions, determine based on existence and revision
                        if (!targetFiles.ContainsKey(targetPath))
                        {
                            operations[sourceFile.Key] = SyncOperation.Add;
                        }
                        else
                        {
                            // File exists in both - check if it needs updating
                            var sourceRev = sourceFile.Value.Revision;
                            var targetRev = targetFiles[targetPath].Revision;
                            
                            if (sourceRev > targetRev)
                            {
                                operations[sourceFile.Key] = SyncOperation.Edit;
                            }
                        }
                        break;
                }
            }

            _logger.LogDebug("Determined {OperationCount} sync operations needed", operations.Count);
            foreach (var op in operations)
            {
                _logger.LogDebug("Operation: {FilePath} -> {Operation}", op.Key, op.Value);
            }
            return operations;
        }

        /// <summary>
        /// Executes the determined sync operations using p4 commands with path translation
        /// </summary>
        private void ExecuteSyncOperations(P4Connection source, P4Connection target,
            Dictionary<string, SyncOperation> operations, string direction, SyncProfile profile)
        {
            try
            {
                // Group operations by type for efficient execution
                var adds = operations.Where(o => o.Value == SyncOperation.Add).Select(o => o.Key).ToList();
                var edits = operations.Where(o => o.Value == SyncOperation.Edit).Select(o => o.Key).ToList();
                var deletes = operations.Where(o => o.Value == SyncOperation.Delete).Select(o => o.Key).ToList();

                _logger.LogDebug("Executing operations - Adds: {AddCount}, Edits: {EditCount}, Deletes: {DeleteCount}", adds.Count, edits.Count, deletes.Count);

                // Execute adds and edits by syncing from source
                if (adds.Any() || edits.Any())
                {
                    var filesToSync = adds.Concat(edits).ToList();
                    var (actualAdds, actualDeletes) = SyncFilesFromSource(source, target, filesToSync, profile);
                    
                    // Add any files that were supposed to be added/edited but don't exist on source to the delete list
                    deletes.AddRange(actualDeletes);
                }

                // Execute deletes by removing from target
                if (deletes.Any())
                {
                    DeleteFilesFromTarget(target, deletes, profile);
                }

                _logger.LogInformation("Executed {OperationCount} operations: {AddCount} adds, {EditCount} edits, {DeleteCount} deletes",
                    operations.Count, adds.Count, edits.Count, deletes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing sync operations");
            }
        }

        /// <summary>
        /// Gets the current revision of a file on the target server
        /// </summary>
        private int GetFileRevision(P4Connection target, string depotPath)
        {
            try
            {
                var args = new List<string> { "files", depotPath };
                var output = ExecuteP4Command(target, args);

                if (string.IsNullOrEmpty(output))
                {
                    return 0;
                }

                // Parse output like: "//depot/path/file.txt#2 - edit change 12345 (text)"
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    var line = lines[0];
                    var hashIndex = line.IndexOf('#');
                    if (hashIndex > 0)
                    {
                        var revisionPart = line.Substring(hashIndex + 1);
                        var spaceIndex = revisionPart.IndexOf(' ');
                        if (spaceIndex > 0)
                        {
                            var revisionStr = revisionPart.Substring(0, spaceIndex);
                            if (int.TryParse(revisionStr, out var revision))
                            {
                                return revision;
                            }
                        }
                    }
                }

                return 0;
            }
            catch
            {
                return 0;
            }
        }
        private bool FileExistsOnTarget(P4Connection target, string depotPath)
        {
            try
            {
                var args = new List<string> { "files", depotPath };
                var output = ExecuteP4Command(target, args);
                return !string.IsNullOrEmpty(output) && 
                       !output.Contains("no such file") && 
                       !output.Contains("not in client view") &&
                       !output.Contains("file(s) not on client");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Applies a P4 action to target using external commands
        /// </summary>
        private bool ApplyP4ActionToTargetExternal(P4Connection source, P4Connection target, string sourceDepotPath, string targetDepotPath, SyncOperation syncOperation, int changelistId, SyncProfile profile, bool targetFileExists)
        {
            if (syncOperation == SyncOperation.Skip)
            {
                _logger.LogDebug("Skipping file {SourceDepotPath} as sync operation is Skip", sourceDepotPath);
                return true;
            }

            try
            {
                _logger.LogDebug("Operating on file {SourceDepotPath} to {TargetDepotPath} with action {SyncOperation}", sourceDepotPath, targetDepotPath, syncOperation);

                if (syncOperation == SyncOperation.Delete)
                {
                    // First sync the file to the target workspace (required for delete)
                    _logger.LogDebug("Syncing target file before delete: {TargetDepotPath}", targetDepotPath);
                    SyncFileToClientExternal(target, targetDepotPath);

                    // Delete the file from target
                    var deleteArgs = new List<string> { "delete" };
                    if (changelistId > 0)
                    {
                        deleteArgs.Add("-c");
                        deleteArgs.Add(changelistId.ToString());
                    }
                    deleteArgs.Add(targetDepotPath);

                    var output = ExecuteP4Command(target, deleteArgs);
                    _logger.LogDebug("Delete result: {Output}", output);
                    return true;
                }

                // Sync the source file to source client workspace
                SyncFileToClientExternal(source, sourceDepotPath);

                // For Edit operations, also sync the target file if it exists in the depot
                if (syncOperation == SyncOperation.Edit && targetFileExists)
                {
                    SyncFileToClientExternal(target, targetDepotPath);
                    _logger.LogDebug("Synced existing target file {TargetDepotPath} to workspace for edit operation", targetDepotPath);
                }

                // Get source client file path
                var sourceClientFilePath = ResolveDepotPathToClientFile(source, sourceDepotPath);
                if (string.IsNullOrEmpty(sourceClientFilePath))
                {
                    _logger.LogWarning("Could not resolve source depot path {SourceDepotPath} to client file path", sourceDepotPath);
                    return false;
                }

                // Get target client file path
                var targetClientFilePath = ResolveDepotPathToClientFile(target, targetDepotPath);
                if (string.IsNullOrEmpty(targetClientFilePath))
                {
                    _logger.LogWarning("Could not resolve target depot path {TargetDepotPath} to client file path", targetDepotPath);
                    return false;
                }

                // Ensure target directory exists
                var directory = Path.GetDirectoryName(targetClientFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogDebug("Created directory: {Directory}", directory);
                }

                // Copy file from source client to target client
                if (sourceClientFilePath != targetClientFilePath)
                {
                    _logger.LogDebug("Copying file - Source: {SourcePath}, Target: {TargetPath}", sourceClientFilePath, targetClientFilePath);
                    System.IO.File.Copy(sourceClientFilePath, targetClientFilePath, true);
                    _logger.LogDebug("Copied file from {Source} to {Target}", sourceClientFilePath, targetClientFilePath);
                }

                // Apply the P4 action
                List<string> actionArgs;
                switch (syncOperation)
                {
                    case SyncOperation.Add:
                        actionArgs = new List<string> { "add" };
                        if (changelistId > 0)
                        {
                            actionArgs.Add("-c");
                            actionArgs.Add(changelistId.ToString());
                        }
                        actionArgs.Add(targetDepotPath);
                        break;
                    case SyncOperation.Edit:
                        // First try to edit the file
                        actionArgs = new List<string> { "edit" };
                        if (changelistId > 0)
                        {
                            actionArgs.Add("-c");
                            actionArgs.Add(changelistId.ToString());
                        }
                        actionArgs.Add(targetDepotPath);
                        
                        var actionOutput = ExecuteP4Command(target, actionArgs);
                        _logger.LogDebug("Edit action result: {Output}", actionOutput);
                        
                        // If edit failed because file is already opened in a different changelist, try reopen
                        if (actionOutput.Contains("can't change from change") || actionOutput.Contains("use 'reopen'"))
                        {
                            _logger.LogDebug("File is already opened in different changelist, trying reopen");
                            actionArgs = new List<string> { "reopen", "-c", changelistId.ToString(), targetDepotPath };
                            actionOutput = ExecuteP4Command(target, actionArgs);
                            _logger.LogDebug("Reopen action result: {Output}", actionOutput);
                        }
                        return true;
                    default:
                        _logger.LogDebug("Unhandled sync operation {Operation} for file {SourcePath}", syncOperation, sourceDepotPath);
                        return false;
                }

                var finalActionOutput = ExecuteP4Command(target, actionArgs);
                _logger.LogDebug("Action result: {Output}", finalActionOutput);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception applying action {Action} to target", syncOperation);
                return false;
            }
        }
        private (List<string> processedFiles, List<string> deletedFiles) SyncFilesFromSource(P4Connection source, P4Connection target, List<string> sourceDepotPaths, SyncProfile profile)
        {
            var processedFiles = new List<string>();
            var deletedFiles = new List<string>();

            try
            {
                foreach (var sourceDepotPath in sourceDepotPaths)
                {
                    _logger.LogDebug("Syncing file: {SourceDepotPath}", sourceDepotPath);

                    // Translate source path to target path for target operations
                    var targetDepotPath = TranslateSourcePathToTarget(sourceDepotPath, profile);
                    _logger.LogDebug("Translated to target path: {TargetDepotPath}", targetDepotPath);

                    // Check if file actually exists and has content before trying to sync
                    var existsCheckArgs = new List<string> { "files", sourceDepotPath };
                    var existsOutput = ExecuteP4Command(source, existsCheckArgs);

                    if (string.IsNullOrEmpty(existsOutput) || 
                        existsOutput.Contains("no such file") || 
                        existsOutput.Contains("delete") ||
                        existsOutput.Contains("not in client view") ||
                        existsOutput.Contains("file(s) not on client"))
                    {
                        _logger.LogDebug("File {DepotPath} does not exist on source (possibly deleted), marking as delete operation", sourceDepotPath);
                        deletedFiles.Add(sourceDepotPath);
                        continue;
                    }

                    processedFiles.Add(sourceDepotPath);

                    // Sync the source file to source client workspace
                    SyncFileToClientExternal(source, sourceDepotPath);

                    // Get source client file path
                    var sourceClientFilePath = ResolveDepotPathToClientFile(source, sourceDepotPath);
                    if (string.IsNullOrEmpty(sourceClientFilePath))
                    {
                        _logger.LogWarning("Could not resolve source depot path {SourceDepotPath} to client file path", sourceDepotPath);
                        continue;
                    }

                    // Check if file exists on target using translated path
                    var targetExists = FileExistsOnTarget(target, targetDepotPath);

                    if (targetExists)
                    {
                        // File exists - open for edit using translated path
                        var editArgs = new List<string> { "edit", targetDepotPath };
                        ExecuteP4Command(target, editArgs);
                        _logger.LogDebug("Opened existing file for edit: {TargetDepotPath}", targetDepotPath);
                    }
                    else
                    {
                        // File doesn't exist - open for add using translated path
                        var addArgs = new List<string> { "add", targetDepotPath };
                        ExecuteP4Command(target, addArgs);
                        _logger.LogDebug("Opened new file for add: {TargetDepotPath}", targetDepotPath);
                    }

                    // Copy the file from source client to target client
                    var targetClientFilePath = ResolveDepotPathToClientFile(target, targetDepotPath);
                    if (!string.IsNullOrEmpty(targetClientFilePath))
                    {
                        // Ensure the target directory exists
                        var directory = Path.GetDirectoryName(targetClientFilePath);
                        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        {
                            Directory.CreateDirectory(directory);
                            _logger.LogDebug("Created directory: {Directory}", directory);
                        }

                        // Copy file from source client to target client
                        System.IO.File.Copy(sourceClientFilePath, targetClientFilePath, true);
                        _logger.LogDebug("Copied file from {Source} to {Target}", sourceClientFilePath, targetClientFilePath);
                    }
                    else
                    {
                        _logger.LogWarning("Could not resolve target depot path {TargetDepotPath} to client file path", targetDepotPath);
                    }
                }

                // Check if there are pending changes and submit them if auto-submit is enabled
                if (profile.AutoSubmit)
                {
                    var openedArgs = new List<string> { "opened" };
                    var openedOutput = ExecuteP4Command(target, openedArgs);

                    if (!string.IsNullOrEmpty(openedOutput) && !openedOutput.Contains("no files"))
                    {
                        _logger.LogDebug("Found opened files, attempting auto-submit");
                        var description = GetChangelistDescription(profile);
                        var submitArgs = new List<string> { "submit", "-d", description };
                        var submitOutput = ExecuteP4Command(target, submitArgs);
                        _logger.LogDebug("Auto-submit result: {Output}", submitOutput);
                    }
                    else
                    {
                        _logger.LogDebug("No opened files to auto-submit");
                    }
                }
                else
                {
                    _logger.LogDebug("Auto-submit disabled for profile {ProfileName}", profile.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing files from source");
            }

            return (processedFiles, deletedFiles);
        }

        /// <summary>
        /// Deletes files from target using translated paths
        /// </summary>
        private void DeleteFilesFromTarget(P4Connection target, List<string> sourceDepotPaths, SyncProfile profile)
        {
            try
            {
                // Translate source paths to target paths for deletion
                var targetDepotPaths = sourceDepotPaths.Select(path => TranslateSourcePathToTarget(path, profile)).ToList();

                var deleteArgs = new List<string> { "delete" };
                deleteArgs.AddRange(targetDepotPaths);

                var output = ExecuteP4Command(target, deleteArgs);
                _logger.LogDebug("Delete output: {Output}", output);

                // Submit the deletions if auto-submit is enabled
                if (profile.AutoSubmit)
                {
                    var submitArgs = new List<string> { "submit", "-d", $"P4Sync deletions {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
                    var submitOutput = ExecuteP4Command(target, submitArgs);
                    _logger.LogDebug("Auto-submit deletions result: {Output}", submitOutput);
                }
                else
                {
                    _logger.LogDebug("Auto-submit disabled for profile {ProfileName}, deletions added to changelist", profile.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting files from target");
            }
        }

        /// <summary>
        /// Executes a p4 command with the specified connection parameters
        /// </summary>
        private (string output, bool success) ExecuteP4CommandWithStatus(P4Connection connection, List<string> args, bool setWorkingDirectory = true)
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "p4.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // Set environment variables for p4 command
            processStartInfo.EnvironmentVariables["P4PORT"] = connection.Port;
            processStartInfo.EnvironmentVariables["P4USER"] = connection.User;
            processStartInfo.EnvironmentVariables["P4CLIENT"] = connection.Workspace;

            // Set working directory to avoid client root issues (but avoid recursion)
            if (setWorkingDirectory)
            {
                try
                {
                    var clientRoot = GetClientRoot(connection);
                    if (!string.IsNullOrEmpty(clientRoot) && Directory.Exists(clientRoot))
                    {
                        processStartInfo.WorkingDirectory = clientRoot;
                    }
                    else
                    {
                        processStartInfo.WorkingDirectory = Path.GetTempPath();
                    }
                }
                catch
                {
                    processStartInfo.WorkingDirectory = Path.GetTempPath();
                }
            }
            else
            {
                processStartInfo.WorkingDirectory = Path.GetTempPath();
            }

            // Add arguments
            foreach (var arg in args)
            {
                processStartInfo.ArgumentList.Add(arg);
            }

            _logger.LogDebug("Executing p4 command: p4 {Args}", string.Join(" ", args));

            using var process = Process.Start(processStartInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start p4.exe process");
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            var success = process.ExitCode == 0;
            if (!success)
            {
                _logger.LogDebug("p4 command failed with exit code {ExitCode}: {Error}", process.ExitCode, error);
            }

            return (output, success);
        }

        /// <summary>
        /// Gets the client root directory for the specified connection
        /// </summary>
        private string GetClientRoot(P4Connection connection)
        {
            try
            {
                var args = new List<string>
                {
                    "client",
                    "-o",
                    connection.Workspace ?? ""
                };

                // Don't set working directory to avoid recursion
                var output = ExecuteP4Command(connection, args, setWorkingDirectory: false);

                // Parse the client spec to find the Root line
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("Root:"))
                    {
                        var root = line.Trim().Substring(5).Trim();
                        _logger.LogDebug("Found client root: {Root}", root);
                        return root;
                    }
                }

                _logger.LogWarning("Could not find Root in client spec for {Workspace}", connection.Workspace);
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error getting client root for {Workspace}", connection.Workspace);
                return string.Empty;
            }
        }

        /// <summary>
        /// Syncs a file from depot to client workspace using p4 sync
        /// </summary>
        private void SyncFileToClientExternal(P4Connection connection, string depotPath)
        {
            try
            {
                var args = new List<string> { "sync", depotPath };
                var output = ExecuteP4Command(connection, args);
                _logger.LogDebug("Synced file {DepotPath} to client", depotPath);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception syncing file {DepotPath}", depotPath);
            }
        }

        /// <summary>
        /// Parses the output of 'p4 files' command into FileInfo objects
        /// </summary>
        private List<P4FileInfo> ParseP4FilesOutput(string output)
        {
            var files = new List<P4FileInfo>();

            if (string.IsNullOrEmpty(output))
            {
                return files;
            }

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                try
                {
                    // Parse line like: "//depot/main/file.txt#1 - add change 12345 (text)"
                    // or: "//depot/main/file.txt#2 - delete change 12345 (text)"
                    var parts = line.Split('#');
                    if (parts.Length >= 2)
                    {
                        var depotPath = parts[0].Trim();
                        
                        // Check if this is a delete action - if so, skip this file
                        if (line.Contains(" - delete "))
                        {
                            _logger.LogDebug("Skipping deleted file: {DepotPath}", depotPath);
                            continue;
                        }
                        
                        // Additional check: if the line contains "no such file" or similar errors, skip
                        if (line.Contains("no such file") || line.Contains("not in client view"))
                        {
                            _logger.LogDebug("Skipping inaccessible file: {DepotPath}", depotPath);
                            continue;
                        }
                        
                        var revisionPart = parts[1].Split(' ')[0];
                        if (int.TryParse(revisionPart, out var revision))
                        {
                            var fileInfo = new P4FileInfo
                            {
                                DepotPath = depotPath,
                                Revision = revision
                            };
                            files.Add(fileInfo);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error parsing p4 files output line: {Line}", line);
                }
            }

            return files;
        }

        /// <summary>
        /// Submits a changelist if it has files, otherwise deletes it (external implementation)
        /// </summary>
        public void SubmitOrDeleteChangelistExternal(P4Connection connection, int changelistId, bool shouldDeleteChangelist)
        {
            try
            {
                // Check if changelist has files
                var openedArgs = new List<string> { "opened", "-c", changelistId.ToString() };
                var openedOutput = ExecuteP4Command(connection, openedArgs);

                bool isChangelistEmpty = string.IsNullOrEmpty(openedOutput) || openedOutput.Contains("no files");

                if (!isChangelistEmpty)
                {
                    // Submit the changelist
                    var submitArgs = new List<string> { "submit", "-c", changelistId.ToString() };
                    var submitOutput = ExecuteP4Command(connection, submitArgs);
                    _logger.LogInformation("Submit completed for changelist {ChangelistId}", changelistId);
                    return;
                }
                else
                {
                    _logger.LogInformation("No files in changelist {ChangelistId}", changelistId);
                }

                if (shouldDeleteChangelist || isChangelistEmpty)
                {
                    // Delete the empty changelist
                    var deleteArgs = new List<string> { "changelist", "-d", changelistId.ToString() };
                    var deleteOutput = ExecuteP4Command(connection, deleteArgs);
                    _logger.LogDebug("Deleted changelist {ChangelistId}", changelistId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting/deleting changelist {ChangelistId}", changelistId);
            }
        }
        /// <summary>
        /// Gets filtered files based on filter patterns (interface implementation)
        /// </summary>
        public List<FileMetaData> GetFilteredFiles(Repository repository, List<string> filterPatterns)
        {
            // For external implementation, we cannot use the Repository parameter as it requires P4 .NET API
            // This method is not supported in external mode. Use ExecuteSync instead which handles filtering internally.
            throw new NotSupportedException("GetFilteredFiles with Repository parameter is not supported in external P4 mode. Use ExecuteSync method instead.");
        }

        /// <summary>
        /// Submits a changelist if it has files, otherwise deletes it (interface implementation)
        /// </summary>
        public bool SubmitOrDeleteChangelist(Repository repo, Changelist changelist, bool shouldDeleteChangelist)
        {
            // For external implementation, we cannot use Repository and Changelist parameters as they require P4 .NET API
            // This method is not supported in external mode. Changelist management is handled internally in ExecuteSync.
            // throw new NotSupportedException("SubmitOrDeleteChangelist with Repository and Changelist parameters is not supported in external P4 mode. Changelist management is handled internally.");
            return false;
        }

        /// <summary>
        /// Translates a source depot path to the corresponding target depot path using workspace-aware path resolution
        /// </summary>
        private string TranslateSourcePathToTarget(string sourcePath, SyncProfile profile)
        {
            if (profile.Source == null || profile.Target == null)
            {
                _logger.LogWarning("Source or target connection is null, using source path as-is");
                return sourcePath;
            }

            try
            {
                // Get source client root
                var sourceClientRoot = GetClientRoot(profile.Source);
                if (string.IsNullOrEmpty(sourceClientRoot))
                {
                    _logger.LogWarning("Cannot determine source client root, using source path as-is");
                    return sourcePath;
                }

                // Get target client root
                var targetClientRoot = GetClientRoot(profile.Target);
                if (string.IsNullOrEmpty(targetClientRoot))
                {
                    _logger.LogWarning("Cannot determine target client root, using source path as-is");
                    return sourcePath;
                }

                // Use p4 where to resolve source depot path to local path
                var sourceLocalPath = ResolveDepotPathToClientFile(profile.Source, sourcePath);
                if (string.IsNullOrEmpty(sourceLocalPath))
                {
                    _logger.LogWarning("Cannot resolve source depot path {SourcePath} to local path, using as-is", sourcePath);
                    return sourcePath;
                }

                // Get relative path from source client root
                var relativePath = Path.GetRelativePath(sourceClientRoot, sourceLocalPath);

                // Apply relative path to target client root
                var targetLocalPath = Path.Combine(targetClientRoot, relativePath);

                // Use p4 where to resolve target local path back to depot path
                var targetDepotPath = ResolveClientFileToDepotPath(profile.Target, targetLocalPath);
                if (string.IsNullOrEmpty(targetDepotPath))
                {
                    _logger.LogWarning("Cannot resolve target local path {TargetLocalPath} to depot path, using source path as-is", targetLocalPath);
                    return sourcePath;
                }

                _logger.LogDebug("Translated source depot path '{SourcePath}' to target depot path '{TargetPath}' using workspace mapping",
                    sourcePath, targetDepotPath);
                return targetDepotPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in workspace-aware path translation, using source path as-is");
                return sourcePath;
            }
        }


        /// <summary>
        /// Resolves a depot path to its corresponding client file path using p4 fstat
        /// </summary>
        private string ResolveDepotPathToClientFile(P4Connection connection, string depotPath)
        {
            try
            {
                var args = new List<string> { "where", depotPath };
                var (output, success) = ExecuteP4CommandWithStatus(connection, args);

                if (!success || string.IsNullOrEmpty(output))
                {
                    return string.Empty;
                }

                // Parse p4 where output: depotPath clientPath
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    var parts = lines[0].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var depotStyleClientPath = parts[1]; // e.g., //workspace/test-files/TestClass.cs
                        _logger.LogDebug("Resolved depot path {DepotPath} to depot-style client path {DepotStylePath}", depotPath, depotStyleClientPath);

                        // Convert depot-style client path to local file system path
                        var clientRoot = GetClientRoot(connection);
                        if (string.IsNullOrEmpty(clientRoot))
                        {
                            _logger.LogWarning("Could not get client root for workspace {Workspace}", connection.Workspace);
                            return string.Empty;
                        }

                        // Remove the //workspace/ prefix and convert to relative path
                        var workspacePrefix = $"//{connection.Workspace}/";
                        if (!depotStyleClientPath.StartsWith(workspacePrefix))
                        {
                            _logger.LogWarning("Depot-style client path {DepotStylePath} does not start with expected workspace prefix {Prefix}",
                                depotStyleClientPath, workspacePrefix);
                            return string.Empty;
                        }

                        var relativePath = depotStyleClientPath.Substring(workspacePrefix.Length);
                        // Convert forward slashes to platform-specific directory separators
                        relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);

                        // Combine with client root to get full local path
                        var localFilePath = Path.Combine(clientRoot, relativePath);
                        _logger.LogDebug("Converted depot-style path {DepotStylePath} to local file path {LocalPath}", depotStyleClientPath, localFilePath);
                        return localFilePath;
                    }
                }

                _logger.LogWarning("Could not parse p4 where output for {DepotPath}", depotPath);
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resolving depot path to client file for {DepotPath}", depotPath);
                return string.Empty;
            }
        }

        /// <summary>
        /// Converts a client file path to a relative path from the client root
        /// </summary>
        private string GetRelativePathFromClientRoot(string clientFilePath, string clientRoot)
        {
            try
            {
                if (!clientFilePath.StartsWith(clientRoot))
                {
                    _logger.LogWarning("Client file path {ClientPath} does not start with client root {ClientRoot}", clientFilePath, clientRoot);
                    return string.Empty;
                }

                var relativePath = clientFilePath.Substring(clientRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                _logger.LogDebug("Converted client path {ClientPath} to relative path {RelativePath} using root {ClientRoot}",
                    clientFilePath, relativePath, clientRoot);
                return relativePath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error converting client path to relative path");
                return string.Empty;
            }
        }

        /// <summary>
        /// Resolves a relative path to a depot path using the target workspace
        /// </summary>
        private string ResolveRelativePathToDepotPath(P4Connection connection, string relativePath, WorkspaceInfo workspaceInfo)
        {
            try
            {
                // Construct the full client path
                var fullClientPath = Path.Combine(workspaceInfo.ClientRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

                // Use p4 where to find the depot path for this client file
                var args = new List<string> { "where", fullClientPath };
                var output = ExecuteP4Command(connection, args);

                if (string.IsNullOrEmpty(output))
                {
                    _logger.LogWarning("p4 where returned no output for client path {ClientPath}", fullClientPath);
                    return string.Empty;
                }

                // Parse p4 where output
                // Format: //depot/path/file.txt //workspace/client/path/file.txt c:\workspace\path\file.txt
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    var parts = lines[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                    {
                        var depotPath = parts[0];
                        _logger.LogDebug("Resolved relative path {RelativePath} to depot path {DepotPath} using client path {ClientPath}",
                            relativePath, depotPath, fullClientPath);
                        return depotPath;
                    }
                }

                _logger.LogWarning("Could not parse p4 where output for client path {ClientPath}", fullClientPath);
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resolving relative path to depot path for {RelativePath}", relativePath);
                return string.Empty;
            }
        }


        /// <summary>
        /// Executes a p4 command and returns the output as a string
        /// </summary>
        private string ExecuteP4Command(P4Connection connection, List<string> args, bool setWorkingDirectory = true)
        {
            var (output, success) = ExecuteP4CommandWithStatus(connection, args, setWorkingDirectory);
            if (!success)
            {
                // Log the error but don't throw an exception for all cases
                _logger.LogWarning("P4 command failed: p4 {Args}", string.Join(" ", args));
            }
            return output;
        }

        /// <summary>
        /// Gets common depot path prefixes from a collection of depot paths
        /// </summary>
        private List<string> GetDepotPathPrefixes(IEnumerable<string> depotPaths)
        {
            var prefixes = new HashSet<string>();

            foreach (var path in depotPaths)
            {
                // Add various levels of prefixes
                var parts = path.TrimStart('/').Split('/');
                if (parts.Length >= 2)
                {
                    // Add //depot/
                    prefixes.Add($"//{parts[0]}/");

                    // Add //depot/level1/
                    if (parts.Length >= 3)
                    {
                        prefixes.Add($"//{parts[0]}/{parts[1]}/");
                    }
                }
            }

            return prefixes.ToList();
        }

        /// <summary>
        /// Gets the relative part of a depot path after the depot name
        /// </summary>
        private string GetRelativeDepotPath(string depotPath)
        {
            // For //depot/main/src/... return main/src/
            var parts = depotPath.TrimStart('/').Split('/');
            if (parts.Length >= 3)
            {
                return string.Join("/", parts.Skip(1)) + "/";
            }
            return string.Empty;
        }

        /// <summary>
        /// Resolves a client file path to its corresponding depot path using p4 where
        /// </summary>
        private string ResolveClientFileToDepotPath(P4Connection connection, string clientFilePath)
        {
            if (connection == null)
            {
                _logger.LogWarning("Connection is null, cannot resolve client file to depot path");
                return string.Empty;
            }

            try
            {
                // Use p4 where with the client file path
                var args = new List<string> { "where", clientFilePath };
                var (output, success) = ExecuteP4CommandWithStatus(connection, args);

                if (!success || string.IsNullOrEmpty(output))
                {
                    _logger.LogWarning("Failed to resolve client file path {ClientPath} to depot path", clientFilePath);
                    return string.Empty;
                }

                // Parse p4 where output: clientPath depotPath
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    var parts = lines[0].Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                    {
                        var depotPath = parts[0]; // The first part is the depot path
                        _logger.LogDebug("Resolved client file path {ClientPath} to depot path {DepotPath}", clientFilePath, depotPath);
                        return depotPath;
                    }
                }

                _logger.LogWarning("Could not parse p4 where output for client file path {ClientPath}", clientFilePath);
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resolving client file path to depot path for {ClientPath}", clientFilePath);
                return string.Empty;
            }
        }
    }
}