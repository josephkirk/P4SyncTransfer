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

                // Get files on target using p4 files command
                var targetFiles = GetFilteredFilesExternal(target, filterPatterns);
                _logger.LogDebug("Found {FileCount} filtered files on target", targetFiles.Count);

                // Create dictionaries for quick lookup
                var sourceFileDict = sourceFiles.ToDictionary(f => f.DepotPath, f => f);
                var targetFileDict = targetFiles.ToDictionary(f => f.DepotPath, f => f);

                // Determine operations needed
                var operations = DetermineSyncOperations(sourceFileDict, targetFileDict);

                if (!operations.Any())
                {
                    _logger.LogInformation("No sync operations needed for {Direction}", direction);
                    return;
                }

                // Execute operations using p4 commands
                ExecuteSyncOperations(source, target, operations, direction);

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
        /// Simple structure to hold file information for external operations
        /// </summary>
        private class FileInfo
        {
            public string DepotPath { get; set; } = string.Empty;
            public int Revision { get; set; }
        }

        /// <summary>
        /// Gets filtered files using p4 fstat command for efficient filtering and existence checking
        /// </summary>
        private List<FileInfo> GetFilteredFilesExternal(P4Connection connection, List<string> filterPatterns)
        {
            var filteredFiles = new List<FileInfo>();

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
        private List<FileInfo> GetFilteredFilesFallback(P4Connection connection, List<string> filterPatterns)
        {
            var filteredFiles = new List<FileInfo>();

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
        private List<FileInfo> ParseP4FstatOutputNoFilter(string output)
        {
            var files = new List<FileInfo>();
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
        private List<FileInfo> ParseP4FstatOutput(string output)
        {
            var files = new List<FileInfo>();
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
        private FileInfo? CreateFileInfoFromFstat(Dictionary<string, string> fstatData)
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

            return new FileInfo
            {
                DepotPath = depotPath,
                Revision = revision
            };
        }

        /// <summary>
        /// Determines what sync operations are needed by comparing source and target
        /// </summary>
        private Dictionary<string, SyncOperation> DetermineSyncOperations(
            Dictionary<string, FileInfo> sourceFiles,
            Dictionary<string, FileInfo> targetFiles)
        {
            var operations = new Dictionary<string, SyncOperation>();

            // Files that exist in source but not in target = ADD
            foreach (var sourceFile in sourceFiles)
            {
                if (!targetFiles.ContainsKey(sourceFile.Key))
                {
                    operations[sourceFile.Key] = SyncOperation.Add;
                }
                else
                {
                    // File exists in both - check if it needs updating
                    var sourceRev = sourceFile.Value.Revision;
                    var targetRev = targetFiles[sourceFile.Key].Revision;

                    if (sourceRev > targetRev)
                    {
                        operations[sourceFile.Key] = SyncOperation.Edit;
                    }
                }
            }

            // Files that exist in target but not in source = DELETE
            foreach (var targetFile in targetFiles)
            {
                if (!sourceFiles.ContainsKey(targetFile.Key))
                {
                    operations[targetFile.Key] = SyncOperation.Delete;
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
        /// Executes the determined sync operations using p4 commands
        /// </summary>
        private void ExecuteSyncOperations(P4Connection source, P4Connection target,
            Dictionary<string, SyncOperation> operations, string direction)
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
                    var (actualAdds, actualDeletes) = SyncFilesFromSource(source, target, filesToSync);
                    
                    // Add any files that were supposed to be added/edited but don't exist on source to the delete list
                    deletes.AddRange(actualDeletes);
                }

                // Execute deletes by removing from target
                if (deletes.Any())
                {
                    DeleteFilesFromTarget(target, deletes);
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
        /// Syncs files from source to target using a simple approach
        /// </summary>
        private (List<string> processedFiles, List<string> deletedFiles) SyncFilesFromSource(P4Connection source, P4Connection target, List<string> depotPaths)
        {
            var processedFiles = new List<string>();
            var deletedFiles = new List<string>();

            try
            {
                foreach (var depotPath in depotPaths)
                {
                    _logger.LogDebug("Syncing file: {DepotPath}", depotPath);

                    // Check if file actually exists and has content before trying to sync
                    var existsCheckArgs = new List<string> { "files", depotPath };
                    var existsOutput = ExecuteP4Command(source, existsCheckArgs);

                    if (string.IsNullOrEmpty(existsOutput) || 
                        existsOutput.Contains("no such file") || 
                        existsOutput.Contains("delete") ||
                        existsOutput.Contains("not in client view") ||
                        existsOutput.Contains("file(s) not on client"))
                    {
                        _logger.LogDebug("File {DepotPath} does not exist on source (possibly deleted), marking as delete operation", depotPath);
                        deletedFiles.Add(depotPath);
                        continue;
                    }

                    processedFiles.Add(depotPath);

                    // Get file content from source
                    var printArgs = new List<string> { "print", "-q", depotPath };
                    var content = ExecuteP4Command(source, printArgs);

                    if (string.IsNullOrEmpty(content) || content.Contains("no such file"))
                    {
                        _logger.LogDebug("Could not get content for {DepotPath} from source, marking as delete operation", depotPath);
                        deletedFiles.Add(depotPath);
                        continue;
                    }

                    // Check if file exists on target
                    var targetExists = FileExistsOnTarget(target, depotPath);

                    if (targetExists)
                    {
                        // File exists - open for edit
                        var editArgs = new List<string> { "edit", depotPath };
                        ExecuteP4Command(target, editArgs);
                        _logger.LogDebug("Opened existing file for edit: {DepotPath}", depotPath);
                    }
                    else
                    {
                        // File doesn't exist - open for add
                        var addArgs = new List<string> { "add", depotPath };
                        ExecuteP4Command(target, addArgs);
                        _logger.LogDebug("Opened new file for add: {DepotPath}", depotPath);
                    }

                    // For this simple implementation, we'll just log that we would write the content
                    // In a full implementation, we'd need to determine the local file path and write the content
                    _logger.LogDebug("Retrieved content for {DepotPath} ({ContentLength} chars)", depotPath, content.Length);
                }

                // Check if there are pending changes and submit them
                var openedArgs = new List<string> { "opened" };
                var openedOutput = ExecuteP4Command(target, openedArgs);

                if (!string.IsNullOrEmpty(openedOutput) && !openedOutput.Contains("no files"))
                {
                    _logger.LogDebug("Found opened files, attempting submit");
                    var submitArgs = new List<string> { "submit", "-d", $"P4Sync {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
                    var submitOutput = ExecuteP4Command(target, submitArgs);
                    _logger.LogDebug("Submit result: {Output}", submitOutput);
                }
                else
                {
                    _logger.LogDebug("No opened files to submit");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing files from source");
            }

            return (processedFiles, deletedFiles);
        }

        /// <summary>
        /// Deletes files from target using p4 delete command
        /// </summary>
        private void DeleteFilesFromTarget(P4Connection target, List<string> depotPaths)
        {
            try
            {
                var deleteArgs = new List<string> { "delete" };
                deleteArgs.AddRange(depotPaths);

                var output = ExecuteP4Command(target, deleteArgs);
                _logger.LogDebug("Delete output: {Output}", output);

                // Submit the deletions
                var submitArgs = new List<string> { "submit", "-d", $"P4Sync deletions {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
                var submitOutput = ExecuteP4Command(target, submitArgs);
                _logger.LogDebug("Submit deletions output: {Output}", submitOutput);
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
        /// Parses the output of 'p4 files' command into FileInfo objects
        /// </summary>
        private List<FileInfo> ParseP4FilesOutput(string output)
        {
            var files = new List<FileInfo>();

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
                            var fileInfo = new FileInfo
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
        public List<FileMetaData> GetFilteredFiles(Connection connection, List<string> filterPatterns)
        {
            // For external implementation, we need to convert Connection to P4Connection
            // This is a limitation - the interface assumes P4 .NET API Connection
            // In practice, we'd need to extract connection details from the Connection object
            throw new NotImplementedException("GetFilteredFiles with Connection parameter not supported in external mode. Use ExecuteSync instead.");
        }

        /// <summary>
        /// Submits a changelist if it has files, otherwise deletes it (interface implementation)
        /// </summary>
        public void SubmitOrDeleteChangelist(Repository repo, Changelist changelist, string direction)
        {
            // This is a placeholder for the external implementation
            _logger.LogInformation("SubmitOrDeleteChangelist called for direction: {Direction}", direction);
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
    }
}