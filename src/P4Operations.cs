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
                var repo = new Repository(server);
                var con = repo.Connection;
                con.UserName = connection.User;
                con.Client = new Client();
                con.Client.Name = connection.Workspace;

                // Connect to the server
                con.Connect(null);

                _logger.LogDebug("Repository and connection created and connected for {Port}", connection.Port);

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
                    sourceClient = GetClientInfo(sourceRepo, profile.Source.Workspace);
                }
                _logger.LogDebug("Source client retrieved: {Result}", sourceClient != null ? "Success" : "Failed");

                _logger.LogDebug("Getting target client: {Workspace}", profile.Target.Workspace);
                if (!string.IsNullOrEmpty(profile.Target.Workspace))
                {
                    targetClient = GetClientInfo(targetRepo, profile.Target.Workspace);
                }
                _logger.LogDebug("Target client retrieved: {Result}", targetClient != null ? "Success" : "Failed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get client information for sync");
                throw;
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

            // Get workspace information for path translation
            _logger.LogDebug("Getting workspace information for path translation");
            var sourceWorkspace = new WorkspaceInfo();
            var targetWorkspace = new WorkspaceInfo();
            var pathMappings = new Dictionary<string, string>();

            if (fromRepo != null && !string.IsNullOrEmpty(profile.Source.Workspace))
            {
                sourceWorkspace = GetWorkspaceInfo(fromRepo, profile.Source.Workspace);
            }
            if (toRepo != null && !string.IsNullOrEmpty(profile.Target.Workspace))
            {
                targetWorkspace = GetWorkspaceInfo(toRepo, profile.Target.Workspace);
            }
            pathMappings = DiscoverAutomaticPathMappings(sourceWorkspace, targetWorkspace);

            _logger.LogDebug("Profile PathMappings count: {Count}", profile.PathMappings?.Count ?? 0);

            // Use explicit path mappings from profile if provided
            if (profile.PathMappings != null && profile.PathMappings.Count > 0)
            {
                pathMappings = profile.PathMappings;
                _logger.LogDebug("Using explicit path mappings from profile: {Mappings}", string.Join(", ", profile.PathMappings.Select(m => $"{m.Key}->{m.Value}")));
            }
            else
            {
                _logger.LogDebug("Using automatically discovered path mappings: {Mappings}", string.Join(", ", pathMappings.Select(m => $"{m.Key}->{m.Value}")));
            }

            _logger.LogInformation("Path mappings for sync: {Mappings}", string.Join(", ", pathMappings.Select(m => $"{m.Key}->{m.Value}")));

            // Translate filter patterns to target paths
            var targetFilterPatterns = TranslateFilterPatternsToTarget(filterPatterns, pathMappings);

                var syncOperations = new Dictionary<string, SyncOperation>();

                // Create changelist for the sync
                _logger.LogDebug("Creating changelist");
                Changelist? changelist = null;
                try
                {
                    if (toRepo != null)
                    {
                        changelist = new Changelist();
                        changelist.Description = GetChangelistDescription(profile);
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
                    targetFilteredFiles = GetFilteredFiles(toConnection, targetFilterPatterns);
                }
                else
                {
                    _logger.LogDebug("Cannot get filtered files - target repository is null");
                }
                _logger.LogDebug("Found {FileCount} target filtered files", targetFilteredFiles.Count);

                // Create dictionaries for quick lookup
                var sourceFileDict = sourceFilteredFiles.ToDictionary(f => f.DepotPath.Path, f => f);
                var targetFileDict = targetFilteredFiles.ToDictionary(f => f.DepotPath.Path, f => f);

                // Determine operations needed (files to add/edit from source)
                var allOperations = new Dictionary<string, SyncOperation>();
                var targetToSourcePathMap = new Dictionary<string, string>(); // Maps target path to source path

                // Files that exist in source but not in target = ADD
                // Files that exist in both but are different = EDIT
                foreach (var sourceFile in sourceFilteredFiles)
                {
                    var sourcePath = sourceFile.DepotPath.Path;
                    var targetPath = TranslateSourcePathToTarget(sourcePath, pathMappings);

                    targetToSourcePathMap[targetPath] = sourcePath;

                    if (!targetFileDict.ContainsKey(targetPath))
                    {
                        allOperations[targetPath] = SyncOperation.Add;
                        _logger.LogDebug("File {SourcePath} (translated to {TargetPath}) will be ADDED to target", sourcePath, targetPath);
                    }
                    else
                    {
                        // File exists in both - check if it needs updating
                        var targetFile = targetFileDict[targetPath];
                        var sourceContents = GetFileContents(fromConnection, sourcePath);
                        var targetContents = GetFileContents(toConnection, targetPath);
                        
                        if (!sourceContents.SequenceEqual(targetContents))
                        {
                            allOperations[targetPath] = SyncOperation.Edit;
                            _logger.LogDebug("File {SourcePath} (translated to {TargetPath}) will be EDITED on target", sourcePath, targetPath);
                        }
                        else
                        {
                            _logger.LogDebug("File {SourcePath} (translated to {TargetPath}) is identical, skipping", sourcePath, targetPath);
                        }
                    }
                }

                // Files that exist in target but not in source = DELETE
                foreach (var targetFile in targetFilteredFiles)
                {
                    var targetPath = targetFile.DepotPath.Path;
                    var sourcePath = TranslateTargetPathToSource(targetPath, pathMappings);

                    if (!sourceFileDict.ContainsKey(sourcePath))
                    {
                        allOperations[targetPath] = SyncOperation.Delete;
                        targetToSourcePathMap[targetPath] = sourcePath; // Even for delete, map it
                        _logger.LogDebug("File {TargetPath} (translated from {SourcePath}) will be DELETED from target", targetPath, sourcePath);
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
                    var targetPath = operation.Key;
                    var syncOp = operation.Value;
                    
                    _logger.LogDebug("Processing {Operation} operation for file: {TargetPath}", syncOp, targetPath);

                    // Get the source path for content retrieval
                    var sourcePath = targetToSourcePathMap.ContainsKey(targetPath) ? targetToSourcePathMap[targetPath] : targetPath;

                    // Get relative path by removing client root (use target client for target operations)
                    var relativePath = string.Empty;
                    if (toClient != null && !string.IsNullOrEmpty(toClient.Root))
                    {
                        relativePath = GetRelativePath(targetPath, toClient.Root);
                    }
                    else
                    {
                        _logger.LogDebug("Cannot get relative path - target client is null or root is empty");
                        relativePath = targetPath; // fallback
                    }

                    // Execute the operation
                    if (syncOp == SyncOperation.Add)
                    {
                        _logger.LogInformation("File does not exist on target, adding...");
                        var fileContents = GetFileContents(fromConnection, sourcePath);
                        if (toConnection != null && fileContents.Any())
                        {
                            AddFileToTarget(toConnection, targetPath, fileContents, changelist);
                        }
                    }
                    else if (syncOp == SyncOperation.Edit)
                    {
                        _logger.LogInformation("File is different, updating...");
                        var fileContents = GetFileContents(fromConnection, sourcePath);
                        if (toConnection != null && fileContents.Any())
                        {
                            EditFileOnTarget(toConnection, targetPath, fileContents, changelist);
                        }
                    }
                    else if (syncOp == SyncOperation.Delete)
                    {
                        _logger.LogInformation("File no longer exists on source, deleting from target...");
                        if (toConnection != null)
                        {
                            DeleteFileFromTarget(toConnection, targetPath, changelist);
                        }
                    }

                    // Track the operation
                    syncOperations[relativePath] = syncOp;
                }

                // Submit the changelist if any files were modified and auto-submit is enabled
                if (toRepo != null && changelist != null && profile.AutoSubmit)
                {
                    SubmitOrDeleteChangelist(toRepo, changelist, direction);
                }
                else
                {
                    if (!profile.AutoSubmit)
                    {
                        _logger.LogDebug("Auto-submit disabled for profile {ProfileName}, changelist left pending", profile.Name);
                    }
                    else
                    {
                        _logger.LogDebug("Cannot submit changelist - repo or changelist is null, or auto-submit disabled");
                    }
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
            // For depot paths like //depot/project/relative, get the relative part
            var pathWithoutDepot = depotPath.StartsWith("//") ? depotPath.Substring(2) : depotPath;
            var firstSlash = pathWithoutDepot.IndexOf('/');
            if (firstSlash >= 0)
            {
                var afterDepot = pathWithoutDepot.Substring(firstSlash + 1);
                var secondSlash = afterDepot.IndexOf('/');
                if (secondSlash >= 0)
                {
                    pathWithoutDepot = afterDepot.Substring(secondSlash + 1);
                }
                else
                {
                    pathWithoutDepot = afterDepot;
                }
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
        /// Gets client information using P4API
        /// </summary>
        private Client? GetClientInfo(Repository repo, string clientName)
        {
            try
            {
                var client = repo.GetClient(clientName);
                if (client != null)
                {
                    _logger.LogDebug("Retrieved client {Client} with root {Root}", clientName, client.Root);
                    return client;
                }
                else
                {
                    _logger.LogWarning("Could not retrieve client {Client} from repository", clientName);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception getting client info for {Client}", clientName);
                return null;
            }
        }

        /// <summary>
        /// Gets workspace client information including root and view mappings using P4API
        /// </summary>
        private WorkspaceInfo GetWorkspaceInfo(Repository repo, string clientName)
        {
            var workspaceInfo = new WorkspaceInfo();

            try
            {
                var client = repo.GetClient(clientName);
                if (client != null)
                {
                    workspaceInfo.ClientRoot = client.Root ?? "";

                    // Parse the view map
                    var viewLines = client.ViewMap.ToString().Split('\n');
                    foreach (var line in viewLines)
                    {
                        var trimmed = line.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                            {
                                var depot = parts[0];
                                var clientPath = parts[1];
                                workspaceInfo.ViewMappings[depot] = clientPath;

                                // Also populate depot to client and client to depot mappings
                                workspaceInfo.DepotToClientMappings[depot] = clientPath;
                                workspaceInfo.ClientToDepotMappings[clientPath] = depot;
                            }
                        }
                    }

                    _logger.LogDebug("Retrieved workspace info for {Client}: Root={Root}, {MappingCount} view mappings",
                        clientName, workspaceInfo.ClientRoot, workspaceInfo.ViewMappings.Count);
                }
                else
                {
                    _logger.LogWarning("Could not retrieve client {Client} from repository", clientName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get workspace info for {Client}", clientName);
            }

            return workspaceInfo;
        }

        /// <summary>
        /// Automatically discovers path mappings by comparing source and target workspace view mappings
        /// </summary>
        private Dictionary<string, string> DiscoverAutomaticPathMappings(WorkspaceInfo source, WorkspaceInfo target)
        {
            var automaticMappings = new Dictionary<string, string>();

            try
            {
                // Get all depot prefixes from source and target
                var sourcePrefixes = GetDepotPathPrefixes(source.ViewMappings.Keys).OrderByDescending(p => p.Length).ToList();
                var targetPrefixes = GetDepotPathPrefixes(target.ViewMappings.Keys);

                // Find matching prefixes, preferring longer (more specific) mappings
                foreach (var sourcePrefix in sourcePrefixes)
                {
                    foreach (var targetPrefix in targetPrefixes)
                    {
                        if (sourcePrefix.Length > 2 && targetPrefix.Length > 2 && sourcePrefix != targetPrefix)
                        {
                            automaticMappings[sourcePrefix] = targetPrefix;
                            _logger.LogDebug("Discovered path mapping: {Source} -> {Target}", sourcePrefix, targetPrefix);
                            break; // Take first match for this source
                        }
                    }
                }

                if (automaticMappings.Count == 0)
                {
                    _logger.LogDebug("No automatic path mappings discovered");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to discover automatic path mappings");
            }

            return automaticMappings;
        }

        /// <summary>
        /// Gets common depot path prefixes from a collection of depot paths
        /// </summary>
        private List<string> GetDepotPathPrefixes(IEnumerable<string> depotPaths)
        {
            var prefixes = new List<string>();
            foreach (var path in depotPaths)
            {
                var parts = path.Split('/');
                if (parts.Length >= 3)
                {
                    var prefix = string.Join("/", parts.Take(3)) + "/";
                    if (!prefixes.Contains(prefix))
                    {
                        prefixes.Add(prefix);
                    }
                }
            }
            return prefixes;
        }

        /// <summary>
        /// Translates a source depot path to the corresponding target depot path using path mappings
        /// </summary>
        private string TranslateSourcePathToTarget(string sourcePath, Dictionary<string, string> pathMappings)
        {
            if (pathMappings == null || pathMappings.Count == 0)
            {
                return sourcePath;
            }

            // Check mappings in order of decreasing key length to match more specific mappings first
            foreach (var mapping in pathMappings.OrderByDescending(m => m.Key.Length))
            {
                if (sourcePath.StartsWith(mapping.Key))
                {
                    var translated = mapping.Value + sourcePath.Substring(mapping.Key.Length);
                    _logger.LogDebug("Translated source path {Source} to {Target}", sourcePath, translated);
                    return translated;
                }
            }

            _logger.LogDebug("No translation found for source path {Source}, using as-is", sourcePath);
            return sourcePath;
        }

        /// <summary>
        /// Translates a target depot path to the corresponding source depot path using path mappings
        /// </summary>
        private string TranslateTargetPathToSource(string targetPath, Dictionary<string, string> pathMappings)
        {
            if (pathMappings == null || pathMappings.Count == 0)
            {
                return targetPath;
            }

            // Check mappings in order of decreasing key length to match more specific mappings first
            foreach (var mapping in pathMappings.OrderByDescending(m => m.Key.Length))
            {
                if (targetPath.StartsWith(mapping.Value))
                {
                    var translated = mapping.Key + targetPath.Substring(mapping.Value.Length);
                    _logger.LogDebug("Translated target path {Target} to {Source}", targetPath, translated);
                    return translated;
                }
            }

            _logger.LogDebug("No translation found for target path {Target}, using as-is", targetPath);
            return targetPath;
        }

        /// <summary>
        /// Translates a list of filter patterns from source to target paths using path mappings
        /// </summary>
        private List<string> TranslateFilterPatternsToTarget(List<string> sourcePatterns, Dictionary<string, string> pathMappings)
        {
            var targetPatterns = new List<string>();

            foreach (var pattern in sourcePatterns)
            {
                var translated = TranslateSourcePathToTarget(pattern, pathMappings);
                targetPatterns.Add(translated);
                _logger.LogDebug("Translated filter pattern {Source} to {Target}", pattern, translated);
            }

            return targetPatterns;
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
        private void AddFileToTarget(Connection connection, string depotPath, List<string> contents, Changelist? changelist)
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
                var changelistArg = changelist != null ? $"-c {changelist.Id}" : "";
                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {connection.Client.Name} add {changelistArg} \"{clientPath}\"";
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
        private void EditFileOnTarget(Connection connection, string depotPath, List<string> contents, Changelist? changelist)
        {
            try
            {
                // Get the relative path and client path
                var relativePath = GetRelativePath(depotPath, connection.Client.Root);
                var clientPath = Path.Combine(connection.Client.Root, relativePath);

                // Use p4 edit to open the file for editing
                var changelistArg = changelist != null ? $"-c {changelist.Id}" : "";
                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {connection.Client.Name} edit {changelistArg} \"{clientPath}\"";
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
        private void DeleteFileFromTarget(Connection connection, string depotPath, Changelist? changelist)
        {
            try
            {
                // Get the relative path and client path
                var relativePath = GetRelativePath(depotPath, connection.Client.Root);
                var clientPath = Path.Combine(connection.Client.Root, relativePath);

                _logger.LogDebug("Deleting file - Depot: {DepotPath}, Client: {ClientPath}", depotPath, clientPath);

                // Use p4 delete to delete the file
                var changelistArg = changelist != null ? $"-c {changelist.Id}" : "";
                var p4Process = new System.Diagnostics.Process();
                p4Process.StartInfo.FileName = "p4";
                p4Process.StartInfo.Arguments = $"-p {connection.Server.Address.Uri} -u {connection.UserName} -c {connection.Client.Name} delete {changelistArg} \"{clientPath}\"";
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
                    // Try submitting with the changelist's built-in submit method
                    changelist.Submit(new Options());
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
