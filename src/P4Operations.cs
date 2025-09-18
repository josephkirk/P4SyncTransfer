using Perforce.P4;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace P4Sync
{
    /// <summary>
    /// Service class that encapsulates all Perforce-related operations
    /// </summary>
    public class P4Operations : IP4Operations
    {
        private readonly ILogger<P4Operations> _logger;

        /// <summary>
        /// Constructor with dependency injection
        /// </summary>
        /// <param name="logger">Logger instance</param>
        public P4Operations(ILogger<P4Operations> logger)
        {
            _logger = logger;
        }
        /// <summary>
        /// Establishes a connection to a Perforce server and returns both Repository and Connection
        /// </summary>
        /// <param name="connection">Connection configuration</param>
        /// <returns>Tuple of Repository and Connection instances</returns>
        public (Repository?, Connection?) ConnectWithConnection(P4Connection connection)
        {
            try
            {
                var server = new Server(new ServerAddress(connection.Port));
                var con = new Connection(server);
                con.UserName = connection.User;
                con.Client = new Client();
                con.Client.Name = connection.Workspace;

                // Create repository and try to associate it with the connection
                var repo = new Repository(server);

                // Try to associate the repository with the connection using reflection
                try
                {
                    var connectionField = repo.GetType().GetField("connection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (connectionField != null)
                    {
                        connectionField.SetValue(repo, con);
                        _logger.LogDebug("Repository associated with connection via reflection for {Port}", connection.Port);
                    }
                    else
                    {
                        var connectionProperty = repo.GetType().GetProperty("Connection", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (connectionProperty != null && connectionProperty.CanWrite)
                        {
                            connectionProperty.SetValue(repo, con);
                            _logger.LogDebug("Repository associated with connection via property for {Port}", connection.Port);
                        }
                        else
                        {
                            _logger.LogDebug("Could not associate repository with connection for {Port}", connection.Port);
                        }
                    }
                }
                catch (Exception assocEx)
                {
                    _logger.LogDebug(assocEx, "Exception associating repository with connection");
                }

                return (repo, con);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error connecting to server {Port}", connection.Port);
                return (null, null);
            }
        }

        /// <summary>
        /// Executes synchronization from source to target repository based on profile configuration
        /// </summary>
        /// <param name="profile">Sync profile configuration containing source, target, and filter information</param>
        public void ExecuteSync(SyncProfile profile)
        {
            _logger.LogDebug("P4Operations.ExecuteSync started for profile: {ProfileName}", profile.Name);

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

            _logger.LogDebug("Attempting to connect to source repository");
            var (sourceRepo, sourceConnection) = ConnectWithConnection(profile.Source);
            _logger.LogDebug("Source repository connection result: {Result}", sourceRepo != null ? "Success" : "Failed");

            _logger.LogDebug("Attempting to connect to target repository");
            var (targetRepo, targetConnection) = ConnectWithConnection(profile.Target);
            _logger.LogDebug("Target repository connection result: {Result}", targetRepo != null ? "Success" : "Failed");

            if (sourceRepo == null || targetRepo == null || sourceConnection == null || targetConnection == null)
            {
                _logger.LogError("Failed to connect to repositories");
                return;
            }

            // Get client information for path resolution
            _logger.LogDebug("Getting client information");
            Client? sourceClient = null;
            Client? targetClient = null;
            try
            {
                _logger.LogDebug("Getting source client: {Workspace}", profile.Source.Workspace);
                if (!string.IsNullOrEmpty(profile.Source.Workspace))
                {
                    sourceClient = GetClientInfo(sourceConnection, profile.Source.Workspace);
                }
                _logger.LogDebug("Source client retrieved: {Result}", sourceClient != null ? "Success" : "Failed");

                _logger.LogDebug("Getting target client: {Workspace}", profile.Target.Workspace);
                if (!string.IsNullOrEmpty(profile.Target.Workspace))
                {
                    targetClient = GetClientInfo(targetConnection, profile.Target.Workspace);
                }
                _logger.LogDebug("Target client retrieved: {Result}", targetClient != null ? "Success" : "Failed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception getting client information, continuing with sync");
                _logger.LogDebug("Exception type: {ExceptionType}", ex.GetType().Name);

                // Create basic client objects if GetClient fails
                if (sourceClient == null)
                {
                    sourceClient = new Client();
                    sourceClient.Name = profile.Source.Workspace;
                    _logger.LogDebug("Created fallback source client");
                }
                if (targetClient == null)
                {
                    targetClient = new Client();
                    targetClient.Name = profile.Target.Workspace;
                    _logger.LogDebug("Created fallback target client");
                }
            }

            // Execute source to target sync
            if (profile.SyncFilter != null && profile.SyncFilter.Any())
            {
                _logger.LogDebug("Executing directional sync with {FilterCount} filter patterns", profile.SyncFilter.Count);
                if (sourceClient != null && targetClient != null)
                {
                    ExecuteDirectionalSync(sourceRepo, targetRepo, sourceConnection, targetConnection, sourceClient, targetClient, profile, profile.SyncFilter, "Source to Target", true);
                }
                else
                {
                    _logger.LogDebug("Cannot execute sync - one or more clients are null");
                }
            }
            else
            {
                _logger.LogWarning("Profile '{ProfileName}' has no filters configured. Skipping sync", profile.Name);
            }
        }

        /// <summary>
        /// Executes directional synchronization from source to target with operation tracking and file mapping
        /// </summary>
        private void ExecuteDirectionalSync(Repository fromRepo, Repository toRepo, Connection fromConnection, Connection toConnection,
            Client fromClient, Client toClient, SyncProfile profile, List<string> filterPatterns, string direction, bool isSourceToTarget)
        {
            try
            {
                _logger.LogDebug("ExecuteDirectionalSync started: {Direction}", direction);
                _logger.LogDebug("fromRepo: {Status}", fromRepo != null ? "OK" : "NULL");
                _logger.LogDebug("toRepo: {Status}", toRepo != null ? "OK" : "NULL");
                _logger.LogDebug("fromClient: {Status}", fromClient != null ? "OK" : "NULL");
                _logger.LogDebug("toClient: {Status}", toClient != null ? "OK" : "NULL");

                _logger.LogInformation("Executing {Direction} sync for profile: {ProfileName}", direction, profile.Name);

                _logger.LogDebug("Filter patterns: {PatternCount} patterns", filterPatterns.Count);

                var syncOperations = new Dictionary<string, SyncOperation>();

                // Create changelist for the sync
                _logger.LogDebug("Creating changelist");
                Changelist? changelist = null;
                try
                {
                    if (toRepo != null)
                    {
                        changelist = toRepo.CreateChangelist(changelist);
                        _logger.LogDebug("Changelist created: {ChangelistId}", changelist != null ? changelist.Id.ToString() : "NULL");
                    }
                    else
                    {
                        _logger.LogDebug("Cannot create changelist - target repository is null");
                        changelist = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to create changelist");
                    _logger.LogDebug("Continuing without changelist - using default changelist");
                    changelist = null;
                }

                // Get filtered files from source and target
                _logger.LogDebug("Getting filtered files from source...");
                var sourceFilteredFiles = new List<FileMetaData>();
                if (fromRepo != null)
                {
                    sourceFilteredFiles = GetFilteredFiles(fromConnection, filterPatterns);
                }
                else
                {
                    _logger.LogDebug("Cannot get filtered files - source repository is null");
                }
                _logger.LogDebug("Found {FileCount} source filtered files", sourceFilteredFiles.Count);

                _logger.LogDebug("Getting filtered files from target...");
                var targetFilteredFiles = new List<FileMetaData>();
                if (toRepo != null)
                {
                    targetFilteredFiles = GetFilteredFiles(toConnection, filterPatterns);
                }
                else
                {
                    _logger.LogDebug("Cannot get filtered files - target repository is null");
                }
                _logger.LogDebug("Found {FileCount} target filtered files", targetFilteredFiles.Count);

                // Create dictionaries for quick lookup (like the working external implementation)
                var sourceFileDict = sourceFilteredFiles.ToDictionary(f => f.DepotPath.Path, f => f);
                var targetFileDict = targetFilteredFiles.ToDictionary(f => f.DepotPath.Path, f => f);

                // Determine operations needed (files to add/edit from source)
                var allOperations = new Dictionary<string, SyncOperation>();

                // Files that exist in source but not in target = ADD
                // Files that exist in both but are different = EDIT
                foreach (var sourceFile in sourceFilteredFiles)
                {
                    var depotPath = sourceFile.DepotPath.Path;
                    if (!targetFileDict.ContainsKey(depotPath))
                    {
                        allOperations[depotPath] = SyncOperation.Add;
                        _logger.LogDebug("File {FilePath} will be ADDED to target", depotPath);
                    }
                    else
                    {
                        // File exists in both - check if it needs updating
                        var sourceContents = GetFileContents(fromConnection, depotPath);
                        var targetContents = GetFileContents(toConnection, depotPath);
                        
                        if (!sourceContents.SequenceEqual(targetContents))
                        {
                            allOperations[depotPath] = SyncOperation.Edit;
                            _logger.LogDebug("File {FilePath} will be EDITED on target", depotPath);
                        }
                        else
                        {
                            _logger.LogDebug("File {FilePath} is identical, skipping", depotPath);
                        }
                    }
                }

                // Files that exist in target but not in source = DELETE
                foreach (var targetFile in targetFilteredFiles)
                {
                    var depotPath = targetFile.DepotPath.Path;
                    if (!sourceFileDict.ContainsKey(depotPath))
                    {
                        allOperations[depotPath] = SyncOperation.Delete;
                        _logger.LogDebug("File {FilePath} will be DELETED from target", depotPath);
                    }
                }

                _logger.LogInformation("Determined {OperationCount} sync operations: {AddCount} adds, {EditCount} edits, {DeleteCount} deletes",
                    allOperations.Count,
                    allOperations.Values.Count(op => op == SyncOperation.Add),
                    allOperations.Values.Count(op => op == SyncOperation.Edit),
                    allOperations.Values.Count(op => op == SyncOperation.Delete));

                // Execute the operations
                foreach (var operation in allOperations)
                {
                    var depotPath = operation.Key;
                    var syncOp = operation.Value;
                    
                    _logger.LogDebug("Processing {Operation} operation for file: {FilePath}", syncOp, depotPath);

                    // Get relative path by removing client root
                    var relativePath = string.Empty;
                    if (fromClient != null && !string.IsNullOrEmpty(fromClient.Root))
                    {
                        relativePath = GetRelativePath(depotPath, fromClient.Root);
                    }
                    else
                    {
                        _logger.LogDebug("Cannot get relative path - source client is null or root is empty");
                        relativePath = depotPath; // fallback to depot path
                    }

                    // Execute the operation
                    if (syncOp == SyncOperation.Add)
                    {
                        _logger.LogInformation("File does not exist on target, adding...");
                        var fileContents = GetFileContents(fromConnection, depotPath);
                        if (toConnection != null && fileContents.Any())
                        {
                            AddFileToTarget(toConnection, depotPath, fileContents);
                        }
                    }
                    else if (syncOp == SyncOperation.Edit)
                    {
                        _logger.LogInformation("File is different, updating...");
                        var fileContents = GetFileContents(fromConnection, depotPath);
                        if (toConnection != null && fileContents.Any())
                        {
                            EditFileOnTarget(toConnection, depotPath, fileContents);
                        }
                    }
                    else if (syncOp == SyncOperation.Delete)
                    {
                        _logger.LogInformation("File no longer exists on source, deleting from target...");
                        if (toConnection != null)
                        {
                            DeleteFileFromTarget(toConnection, depotPath);
                        }
                    }

                    // Track the operation
                    syncOperations[relativePath] = syncOp;
                }

                // Submit the changelist if any files were modified
                if (toRepo != null && changelist != null)
                {
                    SubmitOrDeleteChangelist(toRepo, changelist, direction);
                }
                else
                {
                    _logger.LogDebug("Cannot submit changelist - repo or changelist is null");
                }

                // Log sync operations summary
                _logger.LogInformation("{Direction} sync completed. Operations performed:", direction);
                foreach (var op in syncOperations)
                {
                    _logger.LogInformation("  {FilePath}: {Operation}", op.Key, op.Value);
                }

                _logger.LogInformation("{Direction} sync for profile {ProfileName} completed.", direction, profile.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing {Direction} sync for profile {ProfileName}", direction, profile.Name);
            }
        }

        /// <summary>
        /// Gets the relative path by removing the client root
        /// </summary>
        private string GetRelativePath(string depotPath, string clientRoot)
        {
            // Remove leading //depot/ and client root to get relative path
            var pathWithoutDepot = depotPath.StartsWith("//") ? depotPath.Substring(2) : depotPath;
            if (pathWithoutDepot.Contains('/'))
            {
                pathWithoutDepot = pathWithoutDepot.Substring(pathWithoutDepot.IndexOf('/') + 1);
            }

            // Ensure client root ends with separator
            var normalizedRoot = clientRoot.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? clientRoot
                : clientRoot + Path.DirectorySeparatorChar;

            // If the path starts with client root, remove it
            if (pathWithoutDepot.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return pathWithoutDepot.Substring(normalizedRoot.Length);
            }

            return pathWithoutDepot;
        }

        /// <summary>
        /// Enumeration of sync operations
        /// </summary>
        private enum SyncOperation
        {
            Add,
            Edit,
            Delete,
            Skip
        }

        /// <summary>
        /// Gets client information using external P4 process to avoid P4API issues
        /// </summary>
        private Client? GetClientInfo(Connection connection, string clientName)
        {
            try
            {
                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {clientName} client -o {clientName}";
                p4Process.StartInfo.UseShellExecute = false;
                p4Process.StartInfo.RedirectStandardOutput = true;
                p4Process.StartInfo.RedirectStandardError = true;
                p4Process.StartInfo.CreateNoWindow = true;

                p4Process.Start();
                var output = p4Process.StandardOutput.ReadToEnd();
                var error = p4Process.StandardError.ReadToEnd();
                p4Process.WaitForExit();

                if (p4Process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    // Parse the client spec to extract the Root
                    var client = new Client();
                    client.Name = clientName;

                    var lines = output.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("Root:"))
                        {
                            var root = line.Substring(5).Trim();
                            client.Root = root;
                            // Also set the root on the connection's client
                            if (connection.Client != null)
                            {
                                connection.Client.Root = root;
                            }
                            _logger.LogDebug("Retrieved client root: {Root}", root);
                            break;
                        }
                    }

                    return client;
                }
                else
                {
                    _logger.LogDebug("Failed to get client info: {Error}", error);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception getting client info");
                return null;
            }
        }

        /// <summary>
        /// Gets file contents using external P4 process
        /// </summary>
        private List<string> GetFileContents(Connection connection, string depotPath)
        {
            try
            {
                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {connection.Client.Name} print \"{depotPath}\"";
                p4Process.StartInfo.UseShellExecute = false;
                p4Process.StartInfo.RedirectStandardOutput = true;
                p4Process.StartInfo.RedirectStandardError = true;
                p4Process.StartInfo.CreateNoWindow = true;

                p4Process.Start();
                var output = p4Process.StandardOutput.ReadToEnd();
                var error = p4Process.StandardError.ReadToEnd();
                p4Process.WaitForExit();

                if (p4Process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    // Parse P4 print output - skip the header line
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var contents = new List<string>();

                    // Skip the first line which contains the file info
                    for (int i = 1; i < lines.Length; i++)
                    {
                        contents.Add(lines[i]);
                    }

                    return contents;
                }
                else
                {
                    _logger.LogDebug("Failed to get file contents for {DepotPath}: {Error}", depotPath, error);
                    return new List<string>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception getting file contents for {DepotPath}", depotPath);
                return new List<string>();
            }
        }

        /// <summary>
        /// Checks if a file exists on target using external P4 process
        /// </summary>
        private bool FileExistsOnTarget(Connection connection, string depotPath)
        {
            try
            {
                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {connection.Client.Name} files \"{depotPath}\"";
                p4Process.StartInfo.UseShellExecute = false;
                p4Process.StartInfo.RedirectStandardOutput = true;
                p4Process.StartInfo.RedirectStandardError = true;
                p4Process.StartInfo.CreateNoWindow = true;

                p4Process.Start();
                var output = p4Process.StandardOutput.ReadToEnd();
                var error = p4Process.StandardError.ReadToEnd();
                p4Process.WaitForExit();

                // If the file exists, p4 files will return output, if not, it will have an error
                return p4Process.ExitCode == 0 && !string.IsNullOrEmpty(output) && !error.Contains("no such file");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception checking if file exists {DepotPath}", depotPath);
                return false;
            }
        }

        /// <summary>
        /// Adds a file to target using external P4 process
        /// </summary>
        private void AddFileToTarget(Connection connection, string depotPath, List<string> contents)
        {
            try
            {
                // Create the file in the client workspace first
                var relativePath = GetRelativePath(depotPath, connection.Client.Root);
                var clientPath = Path.Combine(connection.Client.Root, relativePath);

                _logger.LogDebug("Adding file - Depot: {DepotPath}, Client: {ClientPath}, Root: {ClientRoot}", depotPath, clientPath, connection.Client.Root);

                // Ensure directory exists
                var directory = Path.GetDirectoryName(clientPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogDebug("Created directory: {Directory}", directory);
                }

                // Check if file already exists and try to make it writable if needed
                if (System.IO.File.Exists(clientPath))
                {
                    var fileInfo = new FileInfo(clientPath);
                    if (fileInfo.IsReadOnly)
                    {
                        // File is read-only (likely under Perforce control), make it writable
                        fileInfo.IsReadOnly = false;
                        _logger.LogDebug("Made read-only file writable: {ClientPath}", clientPath);
                    }
                    else
                    {
                        // File exists and is writable, delete it
                        System.IO.File.Delete(clientPath);
                        _logger.LogDebug("Deleted existing file: {ClientPath}", clientPath);
                    }
                }

                // Write contents to client workspace
                System.IO.File.WriteAllLines(clientPath, contents);
                _logger.LogDebug("Wrote file contents to: {ClientPath}", clientPath);

                // Use p4 add to add the file
                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {connection.Client.Name} add \"{clientPath}\"";
                p4Process.StartInfo.UseShellExecute = false;
                p4Process.StartInfo.RedirectStandardOutput = true;
                p4Process.StartInfo.RedirectStandardError = true;
                p4Process.StartInfo.CreateNoWindow = true;

                p4Process.Start();
                var output = p4Process.StandardOutput.ReadToEnd();
                var error = p4Process.StandardError.ReadToEnd();
                p4Process.WaitForExit();

                if (p4Process.ExitCode == 0)
                {
                    _logger.LogDebug("Successfully added file {DepotPath} to target", depotPath);

                    // Submit the changelist
                    SubmitChangelist(connection);
                }
                else
                {
                    _logger.LogDebug("Failed to add file {DepotPath}: {Error}", depotPath, error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception adding file {DepotPath}", depotPath);
                _logger.LogDebug("Stack trace: {StackTrace}", ex.StackTrace);
            }
        }

        /// <summary>
        /// Edits a file on target using external P4 process
        /// </summary>
        private void EditFileOnTarget(Connection connection, string depotPath, List<string> contents)
        {
            try
            {
                // Get the relative path and client path
                var relativePath = GetRelativePath(depotPath, connection.Client.Root);
                var clientPath = Path.Combine(connection.Client.Root, relativePath);

                // Use p4 edit to open the file for editing
                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {connection.Client.Name} edit \"{clientPath}\"";
                p4Process.StartInfo.UseShellExecute = false;
                p4Process.StartInfo.RedirectStandardOutput = true;
                p4Process.StartInfo.RedirectStandardError = true;
                p4Process.StartInfo.CreateNoWindow = true;

                p4Process.Start();
                var output = p4Process.StandardOutput.ReadToEnd();
                var error = p4Process.StandardError.ReadToEnd();
                p4Process.WaitForExit();

                if (p4Process.ExitCode == 0)
                {
                    // Now update the file contents
                    System.IO.File.WriteAllLines(clientPath, contents);
                    _logger.LogDebug("Successfully updated file {DepotPath} on target", depotPath);

                    // Submit the changelist
                    SubmitChangelist(connection);
                }
                else
                {
                    _logger.LogDebug("Failed to edit file {DepotPath}: {Error}", depotPath, error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception editing file {DepotPath}", depotPath);
            }
        }

        /// <summary>
        /// Deletes a file from target using external P4 process
        /// </summary>
        private void DeleteFileFromTarget(Connection connection, string depotPath)
        {
            try
            {
                // Get the relative path and client path
                var relativePath = GetRelativePath(depotPath, connection.Client.Root);
                var clientPath = Path.Combine(connection.Client.Root, relativePath);

                _logger.LogDebug("Deleting file - Depot: {DepotPath}, Client: {ClientPath}", depotPath, clientPath);

                // Use p4 delete to delete the file
                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {connection.Client.Name} delete \"{clientPath}\"";
                p4Process.StartInfo.UseShellExecute = false;
                p4Process.StartInfo.RedirectStandardOutput = true;
                p4Process.StartInfo.RedirectStandardError = true;
                p4Process.StartInfo.CreateNoWindow = true;

                p4Process.Start();
                var output = p4Process.StandardOutput.ReadToEnd();
                var error = p4Process.StandardError.ReadToEnd();
                p4Process.WaitForExit();

                if (p4Process.ExitCode == 0)
                {
                    _logger.LogDebug("Successfully deleted file {DepotPath} from target", depotPath);

                    // Submit the changelist
                    SubmitChangelist(connection);
                }
                else
                {
                    _logger.LogDebug("Failed to delete file {DepotPath}: {Error}", depotPath, error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception deleting file {DepotPath}", depotPath);
            }
        }

        /// <summary>
        /// Submits pending changelist using external P4 process
        /// </summary>
        private void SubmitChangelist(Connection connection)
        {
            try
            {
                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {connection.Client.Name} submit -d \"P4Sync automated sync\"";
                p4Process.StartInfo.UseShellExecute = false;
                p4Process.StartInfo.RedirectStandardOutput = true;
                p4Process.StartInfo.RedirectStandardError = true;
                p4Process.StartInfo.CreateNoWindow = true;

                p4Process.Start();
                var output = p4Process.StandardOutput.ReadToEnd();
                var error = p4Process.StandardError.ReadToEnd();
                p4Process.WaitForExit();

                if (p4Process.ExitCode == 0)
                {
                    _logger.LogDebug("Successfully submitted changelist");
                }
                else
                {
                    _logger.LogDebug("Failed to submit changelist: {Error}", error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception submitting changelist");
            }
        }

        /// <summary>
        /// Gets filtered files based on filter patterns using server-side filtering
        /// </summary>
        /// <param name="connection">Connection for authenticated operations</param>
        /// <param name="filterPatterns">Filter patterns to apply</param>
        /// <returns>List of filtered files</returns>
        public List<FileMetaData> GetFilteredFiles(Connection connection, List<string> filterPatterns)
        {
            var filteredFiles = new List<FileMetaData>();

            try
            {
                _logger.LogDebug("Getting filtered files using server-side filtering with {PatternCount} patterns", filterPatterns.Count);

                // Use p4 fstat with filter patterns for efficient server-side filtering
                // This avoids getting all depot files and then filtering client-side
                var fstatArgs = new List<string> { "fstat" };
                fstatArgs.AddRange(filterPatterns);

                _logger.LogDebug("Executing p4 fstat with patterns: {patterns}", string.Join(" ", filterPatterns));

                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {connection.Client.Name} {string.Join(" ", fstatArgs)}";
                p4Process.StartInfo.UseShellExecute = false;
                p4Process.StartInfo.RedirectStandardOutput = true;
                p4Process.StartInfo.RedirectStandardError = true;
                p4Process.StartInfo.CreateNoWindow = true;

                try
                {
                    p4Process.Start();
                    var output = p4Process.StandardOutput.ReadToEnd();
                    var error = p4Process.StandardError.ReadToEnd();
                    p4Process.WaitForExit();

                    if (p4Process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                    {
                        _logger.LogDebug("P4 fstat command succeeded");
                        
                        // Parse fstat output into FileMetaData objects
                        var allFiles = ParseP4FstatOutput(output);
                        filteredFiles.AddRange(allFiles);
                        
                        _logger.LogDebug("Found {FileCount} files matching filter patterns", filteredFiles.Count);
                    }
                    else
                    {
                        _logger.LogWarning("P4 fstat command failed, falling back to files command");
                        // Fallback to p4 files if fstat fails
                        filteredFiles = GetFilteredFilesFallback(connection, filterPatterns);
                    }
                }
                catch (Exception procEx)
                {
                    _logger.LogWarning(procEx, "Exception running P4 fstat process, trying fallback");
                    // Try fallback method
                    try
                    {
                        filteredFiles = GetFilteredFilesFallback(connection, filterPatterns);
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogError(fallbackEx, "Fallback method also failed");
                    }
                }

                _logger.LogInformation("Retrieved {FileCount} files matching filter patterns", filteredFiles.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting filtered files");
            }

            return filteredFiles;
        }

        /// <summary>
        /// Fallback method using p4 files command (less efficient but more compatible)
        /// </summary>
        private List<FileMetaData> GetFilteredFilesFallback(Connection connection, List<string> filterPatterns)
        {
            var filteredFiles = new List<FileMetaData>();

            try
            {
                // Use p4 files with filter patterns
                var filesArgs = new List<string> { "files" };
                filesArgs.AddRange(filterPatterns);

                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {connection.Client.Name} {string.Join(" ", filesArgs)}";
                p4Process.StartInfo.UseShellExecute = false;
                p4Process.StartInfo.RedirectStandardOutput = true;
                p4Process.StartInfo.RedirectStandardError = true;
                p4Process.StartInfo.CreateNoWindow = true;

                p4Process.Start();
                var output = p4Process.StandardOutput.ReadToEnd();
                p4Process.WaitForExit();

                if (p4Process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                {
                    var allFiles = ParseP4FilesOutput(output);
                    filteredFiles.AddRange(allFiles);
                    _logger.LogDebug("Retrieved {FileCount} files using fallback files command", filteredFiles.Count);
                }
                else
                {
                    _logger.LogWarning("P4 files fallback command also failed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in fallback method for getting filtered files");
            }

            return filteredFiles;
        }

        /// <summary>
        /// Submits a changelist if it has files, otherwise deletes it
        /// </summary>
        /// <param name="repo">Repository instance</param>
        /// <param name="changelist">Changelist to submit or delete</param>
        /// <param name="direction">Direction description for logging</param>
        public void SubmitOrDeleteChangelist(Repository repo, Changelist changelist, string direction)
        {
            try
            {
                var changelistInfo = repo.GetChangelist(changelist.Id);
                if (changelistInfo.Files.Count > 0)
                {
                    var submitOptions = new SubmitCmdOptions(SubmitFilesCmdFlags.None, changelist.Id, null, changelist.Description, null);
                    changelist.Submit(submitOptions);
                    _logger.LogInformation("{Direction} sync completed successfully with {FileCount} files.", direction, changelistInfo.Files.Count);
                }
                else
                {
                    _logger.LogInformation("No files to sync from {Direction}.", direction);
                    repo.DeleteChangelist(changelist, new Options());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting {Direction} changelist", direction);
            }
        }

        /// <summary>
        /// Parses the output of 'p4 fstat' command into FileMetaData objects
        /// </summary>
        private List<FileMetaData> ParseP4FstatOutput(string output)
        {
            var files = new List<FileMetaData>();
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
                        var fileInfo = CreateFileMetaDataFromFstat(currentFile);
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
                var fileInfo = CreateFileMetaDataFromFstat(currentFile);
                if (fileInfo != null)
                {
                    files.Add(fileInfo);
                }
            }

            return files;
        }

        /// <summary>
        /// Creates a FileMetaData object from parsed fstat data
        /// </summary>
        private FileMetaData? CreateFileMetaDataFromFstat(Dictionary<string, string> fstatData)
        {
            if (!fstatData.ContainsKey("depotFile"))
            {
                return null;
            }

            var depotPath = fstatData["depotFile"];

            // Only include files that exist (not deleted) - this is the key fix!
            if (fstatData.ContainsKey("headAction") && fstatData["headAction"] == "delete")
            {
                _logger.LogDebug("Skipping deleted file: {DepotPath}", depotPath);
                return null; // Skip deleted files
            }

            var fileMetaData = new FileMetaData();
            fileMetaData.DepotPath = new DepotPath(depotPath);

            // Get revision number if available
            if (fstatData.ContainsKey("headRev"))
            {
                if (int.TryParse(fstatData["headRev"], out var revision))
                {
                    // FileMetaData doesn't have a direct revision property, but we can store it in the action or other field
                    // For now, we just log it
                    _logger.LogDebug("File {DepotPath} revision: {Revision}", depotPath, revision);
                }
            }

            return fileMetaData;
        }

        /// <summary>
        /// Parses the output of 'p4 files' command into FileMetaData objects
        /// </summary>
        private List<FileMetaData> ParseP4FilesOutput(string output)
        {
            var files = new List<FileMetaData>();

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
                            var fileMetaData = new FileMetaData();
                            fileMetaData.DepotPath = new DepotPath(depotPath);
                            files.Add(fileMetaData);
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
    }
}
