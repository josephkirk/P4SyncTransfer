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

        /// <summary>
        /// Constructor with dependency injection
        /// </summary>
        /// <param name="logger">Logger instance</param>
        public P4OperationExternal(ILogger<P4OperationExternal> logger)
        {
            _logger = logger;
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
                _logger.LogDebug("ExecuteDirectionalSync started: {Direction}", direction);
                _logger.LogInformation("Executing {Direction} sync for profile: {ProfileName}", direction, profile.Name);

                // Get filtered files from source using p4 files command
                _logger.LogDebug("Getting filtered files from source...");
                var sourceFiles = GetFilteredFilesExternal(source, filterPatterns);
                _logger.LogDebug("Found {FileCount} filtered files on source", sourceFiles.Count);

                // Get files on target using translated filter patterns
                _logger.LogDebug("Getting filtered files from target using translated patterns...");
                var targetFilterPatterns = TranslateFilterPatternsToTarget(filterPatterns, profile);
                var targetFiles = GetFilteredFilesExternal(target, targetFilterPatterns);
                _logger.LogDebug("Found {FileCount} filtered files on target", targetFiles.Count);

                // Create dictionaries for quick lookup
                var sourceFileDict = sourceFiles.ToDictionary(f => f.DepotPath, f => f);
                var targetFileDict = targetFiles.ToDictionary(f => f.DepotPath, f => f);

                // Determine operations needed
                var operations = DetermineSyncOperations(sourceFileDict, targetFileDict, profile);

                if (!operations.Any())
                {
                    _logger.LogInformation("No sync operations needed for {Direction}", direction);
                    return;
                }

                // Execute operations using p4 commands
                ExecuteSyncOperations(source, target, operations, direction, profile);

                _logger.LogInformation("{Direction} sync for profile {ProfileName} completed.", direction, profile.Name);
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
        /// Gets the changelist description for the sync profile
        /// </summary>
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
        /// Automatically discovers path mappings by comparing source and target workspace view mappings
        /// </summary>
        private Dictionary<string, string> DiscoverAutomaticPathMappings(P4Connection source, P4Connection target)
        {
            var automaticMappings = new Dictionary<string, string>();

            try
            {
                var sourceWorkspace = GetWorkspaceInfo(source);
                var targetWorkspace = GetWorkspaceInfo(target);

                // First, try to discover mappings based on streams
                var streamMappings = DiscoverStreamBasedMappings(source, target);
                if (streamMappings.Count > 0)
                {
                    automaticMappings = streamMappings;
                    _logger.LogInformation("Auto-discovered {Count} stream-based path mappings", automaticMappings.Count);
                    foreach (var mapping in automaticMappings)
                    {
                        _logger.LogInformation("  {Source} -> {Target}", mapping.Key, mapping.Value);
                    }
                    return automaticMappings;
                }

                // Fallback to view-based discovery if stream discovery didn't work
                if (sourceWorkspace.ViewMappings.Count == 0 || targetWorkspace.ViewMappings.Count == 0)
                {
                    _logger.LogWarning("Could not retrieve view mappings for automatic path discovery");
                    return automaticMappings;
                }

                _logger.LogDebug("Discovering automatic path mappings:");
                _logger.LogDebug("Source workspace has {Count} view mappings", sourceWorkspace.ViewMappings.Count);
                _logger.LogDebug("Target workspace has {Count} view mappings", targetWorkspace.ViewMappings.Count);

                // Find common depot path patterns and create mappings
                foreach (var sourceMapping in sourceWorkspace.ViewMappings)
                {
                    var sourceDepotPath = sourceMapping.Key;
                    var sourceClientPath = sourceMapping.Value;

                    // Try to find a corresponding target mapping
                    foreach (var targetMapping in targetWorkspace.ViewMappings)
                    {
                        var targetDepotPath = targetMapping.Key;
                        var targetClientPath = targetMapping.Value;

                        // Check if the client paths are similar (relative to their respective roots)
                        var sourceRelativePath = GetRelativePathFromClientRoot(sourceClientPath, sourceWorkspace.ClientRoot);
                        var targetRelativePath = GetRelativePathFromClientRoot(targetClientPath, targetWorkspace.ClientRoot);

                        if (!string.IsNullOrEmpty(sourceRelativePath) && !string.IsNullOrEmpty(targetRelativePath) &&
                            sourceRelativePath == targetRelativePath)
                        {
                            // Found a matching relative path - create automatic mapping
                            automaticMappings[sourceDepotPath] = targetDepotPath;
                            _logger.LogDebug("Auto-discovered mapping: {SourceDepot} -> {TargetDepot} (relative path: {RelativePath})",
                                sourceDepotPath, targetDepotPath, sourceRelativePath);
                            break;
                        }
                    }
                }

                // Also try to find mappings based on depot path patterns
                var sourceDepotPrefixes = GetDepotPathPrefixes(sourceWorkspace.ViewMappings.Keys);
                var targetDepotPrefixes = GetDepotPathPrefixes(targetWorkspace.ViewMappings.Keys);

                foreach (var sourcePrefix in sourceDepotPrefixes)
                {
                    foreach (var targetPrefix in targetDepotPrefixes)
                    {
                        // Check if the relative parts match
                        var sourceRelative = GetRelativeDepotPath(sourcePrefix);
                        var targetRelative = GetRelativeDepotPath(targetPrefix);

                        if (sourceRelative == targetRelative && sourceRelative.Length > 0)
                        {
                            automaticMappings[sourcePrefix] = targetPrefix;
                            _logger.LogDebug("Auto-discovered prefix mapping: {SourcePrefix} -> {TargetPrefix}",
                                sourcePrefix, targetPrefix);
                        }
                    }
                }

                _logger.LogInformation("Auto-discovered {Count} path mappings", automaticMappings.Count);
                foreach (var mapping in automaticMappings)
                {
                    _logger.LogInformation("  {Source} -> {Target}", mapping.Key, mapping.Value);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during automatic path mapping discovery");
            }

            return automaticMappings;
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

            // Only include files that exist (not deleted)
            if (fstatData.ContainsKey("headAction") && fstatData["headAction"] == "delete")
            {
                return null; // Skip deleted files
            }

            return new P4FileInfo
            {
                DepotPath = depotPath,
                Revision = revision
            };
        }

        /// <summary>
        /// Determines what sync operations are needed by comparing source and target files with path translation
        /// </summary>
        private Dictionary<string, SyncOperation> DetermineSyncOperations(
            Dictionary<string, P4FileInfo> sourceFiles,
            Dictionary<string, P4FileInfo> targetFiles,
            SyncProfile profile)
        {
            var operations = new Dictionary<string, SyncOperation>();

            // Create a mapping from target paths back to source paths for comparison
            var targetToSourceMapping = new Dictionary<string, string>();
            foreach (var sourceFile in sourceFiles)
            {
                var targetPath = TranslateSourcePathToTarget(sourceFile.Key, profile);
                targetToSourceMapping[targetPath] = sourceFile.Key;
            }

            // Files that exist in source but not in target = ADD
            foreach (var sourceFile in sourceFiles)
            {
                var targetPath = TranslateSourcePathToTarget(sourceFile.Key, profile);
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
            }

            // Files that exist in target but not in source = DELETE
            foreach (var targetFile in targetFiles)
            {
                if (!targetToSourceMapping.ContainsKey(targetFile.Key))
                {
                    // This target file doesn't correspond to any source file
                    // We need to map it back to a source path for the operation
                    var sourcePath = TranslateTargetPathToSource(targetFile.Key, profile);
                    operations[sourcePath] = SyncOperation.Delete;
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
        /// Checks if a file exists on target server
        /// </summary>
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
        /// Syncs files from source to target using translated paths
        /// </summary>
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
        /// Gets filtered files based on filter patterns (interface implementation)
        /// </summary>
        public List<FileMetaData> GetFilteredFiles(Repository repository, List<string> filterPatterns)
        {
            // For external implementation, we need to convert Connection to P4Connection
            // This is a limitation - the interface assumes P4 .NET API Connection
            // In practice, we'd need to extract connection details from the Connection object
            throw new NotImplementedException("GetFilteredFiles with Connection parameter not supported in external mode. Use ExecuteSync instead.");
        }

        /// <summary>
        /// Submits a changelist if it has files, otherwise deletes it (interface implementation)
        /// </summary>
        public void SubmitOrDeleteChangelist(Repository repo, Changelist changelist, bool shouldDeleteChangelist)
        {
            // This is a placeholder for the external implementation
            _logger.LogInformation("SubmitOrDeleteChangelist called for shouldDeleteChangelist: {ShouldDelete}", shouldDeleteChangelist);
        }

        /// <summary>
        /// Translates a source depot path to the corresponding target depot path using workspace-aware path resolution
        /// </summary>
        private string TranslateSourcePathToTarget(string sourcePath, SyncProfile profile)
        {
            // Check if explicit path mappings are configured
            var pathMappings = profile.PathMappings;

            // If no explicit mappings, try to discover automatic mappings
            if (pathMappings == null || pathMappings.Count == 0)
            {
                if (profile.Source != null && profile.Target != null)
                {
                    _logger.LogDebug("No explicit PathMappings configured, attempting automatic discovery");
                    pathMappings = DiscoverAutomaticPathMappings(profile.Source, profile.Target);

                    if (pathMappings.Count == 0)
                    {
                        _logger.LogWarning("No automatic path mappings could be discovered, using source path as-is");
                        return sourcePath;
                    }
                }
                else
                {
                    _logger.LogWarning("Cannot perform automatic path mapping discovery: source or target connection is null");
                    return sourcePath;
                }
            }

            try
            {
                // Check if source and target connections are available
                if (profile.Source == null || profile.Target == null)
                {
                    _logger.LogWarning("Source or target connection is null, falling back to simple path mapping");
                    return TranslateSourcePathToTargetSimple(sourcePath, pathMappings);
                }

                // Get workspace information for source and target
                var sourceWorkspace = GetWorkspaceInfo(profile.Source);
                var targetWorkspace = GetWorkspaceInfo(profile.Target);

                if (string.IsNullOrEmpty(sourceWorkspace.ClientRoot) || string.IsNullOrEmpty(targetWorkspace.ClientRoot))
                {
                    _logger.LogWarning("Could not retrieve workspace information, falling back to simple path mapping");
                    return TranslateSourcePathToTargetSimple(sourcePath, pathMappings);
                }

                // Step 1: Resolve depot path to client file path using fstat
                var clientFilePath = ResolveDepotPathToClientFile(profile.Source, sourcePath);
                if (string.IsNullOrEmpty(clientFilePath))
                {
                    _logger.LogWarning("Could not resolve depot path {DepotPath} to client file, falling back to simple mapping", sourcePath);
                    return TranslateSourcePathToTargetSimple(sourcePath, pathMappings);
                }

                // Step 2: Convert client file path to relative path using source client root
                var relativePath = GetRelativePathFromClientRoot(clientFilePath, sourceWorkspace.ClientRoot);
                if (string.IsNullOrEmpty(relativePath))
                {
                    _logger.LogWarning("Could not convert client path {ClientPath} to relative path, falling back to simple mapping", clientFilePath);
                    return TranslateSourcePathToTargetSimple(sourcePath, pathMappings);
                }

                // Step 3: Convert relative path back to depot path using target workspace
                var targetDepotPath = ResolveRelativePathToDepotPath(profile.Target, relativePath, targetWorkspace);
                if (string.IsNullOrEmpty(targetDepotPath))
                {
                    _logger.LogWarning("Could not resolve relative path {RelativePath} to target depot path, falling back to simple mapping", relativePath);
                    return TranslateSourcePathToTargetSimple(sourcePath, pathMappings);
                }

                _logger.LogDebug("Workspace-aware translation: {SourcePath} -> {ClientPath} -> {RelativePath} -> {TargetPath}",
                    sourcePath, clientFilePath, relativePath, targetDepotPath);

                return targetDepotPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in workspace-aware path translation, falling back to simple mapping");
                return TranslateSourcePathToTargetSimple(sourcePath, pathMappings);
            }
        }

        /// <summary>
        /// Simple fallback path translation using string replacement
        /// </summary>
        private string TranslateSourcePathToTargetSimple(string sourcePath, Dictionary<string, string> pathMappings)
        {
            if (pathMappings == null || pathMappings.Count == 0)
            {
                return sourcePath; // No mappings defined, return as-is
            }

            // Find the most specific mapping that matches the source path
            foreach (var mapping in pathMappings.OrderByDescending(m => m.Key.Length))
            {
                if (sourcePath.StartsWith(mapping.Key))
                {
                    var translatedPath = mapping.Value + sourcePath.Substring(mapping.Key.Length);
                    _logger.LogDebug("Translated source path '{SourcePath}' to target path '{TargetPath}' using mapping '{MappingKey}' -> '{MappingValue}'",
                        sourcePath, translatedPath, mapping.Key, mapping.Value);
                    return translatedPath;
                }
            }

            // No mapping found, return original path
            _logger.LogDebug("No path mapping found for source path '{SourcePath}', using as-is", sourcePath);
            return sourcePath;
        }

        /// <summary>
        /// Resolves a depot path to its corresponding client file path using p4 fstat
        /// </summary>
        private string ResolveDepotPathToClientFile(P4Connection connection, string depotPath)
        {
            try
            {
                var args = new List<string> { "fstat", depotPath };
                var (output, success) = ExecuteP4CommandWithStatus(connection, args);

                if (!success || string.IsNullOrEmpty(output))
                {
                    return string.Empty;
                }

                // Parse fstat output to find clientFile
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("... clientFile "))
                    {
                        var clientFile = line.Trim().Substring("... clientFile ".Length);
                        _logger.LogDebug("Resolved depot path {DepotPath} to client file {ClientFile}", depotPath, clientFile);
                        return clientFile;
                    }
                }

                _logger.LogWarning("Could not find clientFile in fstat output for {DepotPath}", depotPath);
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
        /// Translates a target depot path to the corresponding source depot path using workspace-aware path resolution
        /// </summary>
        private string TranslateTargetPathToSource(string targetPath, SyncProfile profile)
        {
            // Check if explicit path mappings are configured
            var pathMappings = profile.PathMappings;

            // If no explicit mappings, try to discover automatic mappings
            if (pathMappings == null || pathMappings.Count == 0)
            {
                if (profile.Source != null && profile.Target != null)
                {
                    _logger.LogDebug("No explicit PathMappings configured, attempting automatic discovery for reverse translation");
                    pathMappings = DiscoverAutomaticPathMappings(profile.Source, profile.Target);

                    if (pathMappings.Count == 0)
                    {
                        _logger.LogWarning("No automatic path mappings could be discovered, using target path as-is");
                        return targetPath;
                    }
                }
                else
                {
                    _logger.LogWarning("Cannot perform automatic path mapping discovery: source or target connection is null");
                    return targetPath;
                }
            }

            try
            {
                // Check if source and target connections are available
                if (profile.Source == null || profile.Target == null)
                {
                    _logger.LogWarning("Source or target connection is null, falling back to simple path mapping");
                    return TranslateTargetPathToSourceSimple(targetPath, pathMappings);
                }

                // Get workspace information for source and target
                var sourceWorkspace = GetWorkspaceInfo(profile.Source);
                var targetWorkspace = GetWorkspaceInfo(profile.Target);

                if (string.IsNullOrEmpty(sourceWorkspace.ClientRoot) || string.IsNullOrEmpty(targetWorkspace.ClientRoot))
                {
                    _logger.LogWarning("Could not retrieve workspace information, falling back to simple path mapping");
                    return TranslateTargetPathToSourceSimple(targetPath, pathMappings);
                }

                // Step 1: Resolve depot path to client file path using fstat on target
                var clientFilePath = ResolveDepotPathToClientFile(profile.Target, targetPath);
                if (string.IsNullOrEmpty(clientFilePath))
                {
                    _logger.LogWarning("Could not resolve target depot path {DepotPath} to client file, falling back to simple mapping", targetPath);
                    return TranslateTargetPathToSourceSimple(targetPath, pathMappings);
                }

                // Step 2: Convert client file path to relative path using target client root
                var relativePath = GetRelativePathFromClientRoot(clientFilePath, targetWorkspace.ClientRoot);
                if (string.IsNullOrEmpty(relativePath))
                {
                    _logger.LogWarning("Could not convert target client path {ClientPath} to relative path, falling back to simple mapping", clientFilePath);
                    return TranslateTargetPathToSourceSimple(targetPath, pathMappings);
                }

                // Step 3: Convert relative path back to depot path using source workspace
                var sourceDepotPath = ResolveRelativePathToDepotPath(profile.Source, relativePath, sourceWorkspace);
                if (string.IsNullOrEmpty(sourceDepotPath))
                {
                    _logger.LogWarning("Could not resolve relative path {RelativePath} to source depot path, falling back to simple mapping", relativePath);
                    return TranslateTargetPathToSourceSimple(targetPath, pathMappings);
                }

                _logger.LogDebug("Workspace-aware reverse translation: {TargetPath} -> {ClientPath} -> {RelativePath} -> {SourcePath}",
                    targetPath, clientFilePath, relativePath, sourceDepotPath);

                return sourceDepotPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in workspace-aware reverse path translation, falling back to simple mapping");
                return TranslateTargetPathToSourceSimple(targetPath, pathMappings);
            }
        }

        /// <summary>
        /// Simple fallback reverse path translation using string replacement
        /// </summary>
        private string TranslateTargetPathToSourceSimple(string targetPath, Dictionary<string, string> pathMappings)
        {
            if (pathMappings == null || pathMappings.Count == 0)
            {
                return targetPath; // No mappings defined, return as-is
            }

            // Find the most specific mapping that matches the target path (reverse lookup)
            foreach (var mapping in pathMappings.OrderByDescending(m => m.Value.Length))
            {
                if (targetPath.StartsWith(mapping.Value))
                {
                    var translatedPath = mapping.Key + targetPath.Substring(mapping.Value.Length);
                    _logger.LogDebug("Translated target path '{TargetPath}' to source path '{SourcePath}' using reverse mapping '{MappingValue}' -> '{MappingKey}'",
                        targetPath, translatedPath, mapping.Value, mapping.Key);
                    return translatedPath;
                }
            }

            // No mapping found, return original path
            _logger.LogDebug("No reverse path mapping found for target path '{TargetPath}', using as-is", targetPath);
            return targetPath;
        }

        /// <summary>
        /// Translates a list of filter patterns from source to target paths using workspace-aware resolution
        /// </summary>
        private List<string> TranslateFilterPatternsToTarget(List<string> sourcePatterns, SyncProfile profile)
        {
            // Check if explicit path mappings are configured
            var pathMappings = profile.PathMappings;

            // If no explicit mappings, try to discover automatic mappings
            if (pathMappings == null || pathMappings.Count == 0)
            {
                if (profile.Source != null && profile.Target != null)
                {
                    _logger.LogDebug("No explicit PathMappings configured, attempting automatic discovery for filter patterns");
                    pathMappings = DiscoverAutomaticPathMappings(profile.Source, profile.Target);
                }
            }

            var translatedPatterns = new List<string>();

            foreach (var pattern in sourcePatterns)
            {
                try
                {
                    // For workspace-aware translation, we need to translate the depot path part
                    // For patterns like "//depot/main/src/....cs", we translate the base path
                    var translatedPattern = TranslateSourcePathToTargetSimple(pattern, pathMappings ?? new Dictionary<string, string>());
                    translatedPatterns.Add(translatedPattern);

                    _logger.LogDebug("Translated filter pattern '{SourcePattern}' to '{TargetPattern}'",
                        pattern, translatedPattern);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error translating filter pattern '{Pattern}', using original", pattern);
                    translatedPatterns.Add(pattern);
                }
            }

            return translatedPatterns;
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
    }
}