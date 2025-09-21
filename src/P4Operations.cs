using Perforce.P4;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Collections;

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
                        throw new InvalidOperationException("Cannot create changelist - target repository is null");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to create changelist");
                    _logger.LogDebug("Continuing without changelist - using default changelist");
                    changelist = null;
                    throw new InvalidOperationException("Failed to create changelist");
                }


                // Create dictionaries for quick lookup

                // Determine operations needed (files to add/edit from source)
                var sourceToTargetMapping = new Dictionary<FileSpec, FileSpec>(); // Maps source path to target path

                // We get all files we need to process using GetFileMetaData with the filter patterns

                // Get source files
                _logger.LogDebug("Getting source files");
                var sourceFiles = GetFilteredFiles(fromRepo, filterPatterns);
                // for each source file, convert them to relative path using source client root then resolve them to absolute path using target client root
                foreach (var sourceFile in sourceFiles)
                {
                    var sourceLocalPath = sourceFile.LocalPath.Path;

                    // Convert source local path to relative path using source client root
                    var sourceRelativePath = Path.GetRelativePath(fromClient.Root, sourceLocalPath);

                    // Resolve relative path to target absolute path using target client root
                    var targetAbsolutePath = Path.Combine(toClient.Root, sourceRelativePath);

                    _logger.LogDebug("Mapped source file {SourcePath} to target path {TargetPath}", sourceFile.DepotPath.Path, targetAbsolutePath);
                    // use GetFileMetaData to resolve target local path to depot path
                    var targetFileSpec = toRepo.GetFileMetaData(null, new FileSpec(new LocalPath(targetAbsolutePath))).FirstOrDefault();

                    sourceToTargetMapping[sourceFile] = targetFileSpec;

                    FileAction sourceHeadAction = sourceFile.HeadAction;

                    switch (sourceHeadAction)
                    {
                        case FileAction.Add or FileAction.MoveAdd:
                            AddFileToTarget(fromConnection, fromClient, toConnection, toClient, sourceFile.DepotPath.Path, targetAbsolutePath, changelist);
                            break;
                        case FileAction.Edit or FileAction.Integrate:
                            EditFileOnTarget(fromConnection, fromClient, toConnection, toClient, sourceFile.DepotPath.Path, targetAbsolutePath, changelist);
                            break;
                        case FileAction.Delete or FileAction.MoveDelete:
                            DeleteFileOnTarget(toConnection, targetFileSpec?.DepotPath.Path ?? targetAbsolutePath, changelist);
                            break;
                        default:
                            break;
                    }
                    
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
            return Path.GetRelativePath(clientRoot, depotPath);
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
        /// Syncs a file from depot to client workspace using external P4 process
        /// </summary>
        private void SyncFileToClient(Connection connection, string depotPath, SyncFilesCmdFlags syncFilesCmdFlags = SyncFilesCmdFlags.None)
        {
            try
            {

                var syncedfiles = connection.Client.SyncFiles( new SyncFilesCmdOptions(syncFilesCmdFlags), new FileSpec(new DepotPath(depotPath)));

                if (syncedfiles != null && syncedfiles.Count > 0)
                {
                    _logger.LogDebug("Successfully synced file {DepotPath} to client", depotPath);
                }
                else
                {
                    _logger.LogDebug("No files were synced for {DepotPath}", depotPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception syncing file {DepotPath}", depotPath);
            }
        }

        /// <summary>
        /// Checks if a file exists on target using external P4 process
        /// </summary>
        private bool FileExistsOnTarget(Repository repo, string depotPath)
        {
            try
            {
                var fileSpecs = repo.GetFileMetaData(new Options(), new FileSpec(new DepotPath(depotPath)));
                if (fileSpecs == null || fileSpecs.Count == 0)
                {
                    _logger.LogDebug("File {DepotPath} does not exist on target", depotPath);
                    return false;
                }
                return fileSpecs[0].HeadRev > 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception checking if file exists {DepotPath}", depotPath);
                return false;
            }
        }

        /// <summary>
        /// Adds a file to target by copying from source client to target client
        /// </summary>
        private void AddFileToTarget(Connection fromConnection, Client fromClient, Connection toConnection, Client toClient, string sourcePath, string targetPath, Changelist? changelist)
        {
            if (fromClient == null || toClient == null)
            {
                _logger.LogDebug("Cannot add file - source or target client is null");
                return;
            }

            try
            {
                // Sync the source file to source client workspace
                SyncFileToClient(fromConnection, sourcePath);

                // Get client paths
                var sourceRelativePath = GetRelativePath(sourcePath, fromClient.Root);
                var sourceClientPath = Path.Combine(fromClient.Root, sourceRelativePath);
                var targetRelativePath = GetRelativePath(targetPath, toClient.Root);
                var targetClientPath = Path.Combine(toClient.Root, targetRelativePath);

                if (!System.IO.File.Exists(sourceClientPath))
                {
                    throw new FileNotFoundException("Source file not found in source client workspace", sourceClientPath);
                }
                // Ensure target directory exists
                var directory = Path.GetDirectoryName(targetClientPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogDebug("Created directory: {Directory}", directory);
                }

                // Check if target file already exists and handle it
                if (System.IO.File.Exists(targetClientPath))
                {
                    var fileInfo = new FileInfo(targetClientPath);
                    if (fileInfo.IsReadOnly)
                    {
                        // File is read-only (likely under Perforce control), make it writable
                        fileInfo.IsReadOnly = false;
                        _logger.LogDebug("Made read-only file writable: {TargetPath}", targetClientPath);
                    }
                }
                
                
                // Copy file from source client to target client
                if (sourceClientPath != targetClientPath)
                {
                    _logger.LogDebug("Copying file - Source: {SourcePath}, Target: {TargetPath}", sourceClientPath, targetClientPath);
                    System.IO.File.Copy(sourceClientPath, targetClientPath, true);
                    _logger.LogDebug("Copied file from {Source} to {Target}", sourceClientPath, targetClientPath);
                };
                
                var addfiles = toConnection.Client.AddFiles( new Options(AddFilesCmdFlags.None, changelist.Id, null), new FileSpec(new LocalPath(targetClientPath)));

                if (addfiles.Count > 0)
                {
                    _logger.LogDebug("Successfully added file {TargetPath} to target", targetPath);
                }
                else
                {
                    _logger.LogDebug("Failed to add file {TargetPath} to target", targetPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception adding file {TargetPath}", targetPath);
                _logger.LogDebug("Stack trace: {StackTrace}", ex.StackTrace);
            }
        }

        /// <summary>
        /// Edits a file on target by copying from source client to target client
        /// </summary>
        private void EditFileOnTarget(Connection fromConnection, Client fromClient, Connection toConnection, Client toClient, string sourcePath, string targetPath, Changelist? changelist)
        {
            if (fromClient == null || toClient == null)
            {
                _logger.LogDebug("Cannot edit file - source or target client is null");
                return;
            }

            try
            {
                // Sync the source file to source client workspace
                SyncFileToClient(fromConnection, sourcePath);

                // Get client paths
                var sourceRelativePath = GetRelativePath(sourcePath, fromClient.Root);
                var sourceClientPath = Path.Combine(fromClient.Root, sourceRelativePath);
                var targetRelativePath = GetRelativePath(targetPath, toClient.Root);
                var targetClientPath = Path.Combine(toClient.Root, targetRelativePath);

                var editFiles = toConnection.Client.EditFiles( new Options(EditFilesCmdFlags.None, changelist.Id, null), new FileSpec(new LocalPath(targetClientPath)));

                if (editFiles.Count > 0)
                {
                    // Copy file from source client to target client (overwrites the existing file)
                    System.IO.File.Copy(sourceClientPath, targetClientPath, true);
                    _logger.LogDebug("Successfully updated file {TargetPath} on target", targetPath);
                }
                else
                {
                    _logger.LogDebug("Failed to edit file {TargetPath} on target", targetPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception editing file {TargetPath}", targetPath);
            }
        }

        /// <summary>
        /// Deletes a file from target using external P4 process
        /// </summary>
        private void DeleteFileOnTarget(Connection connection, string depotPath, Changelist? changelist)
        {
            try
            {
                // Get the relative path and client path
                var relativePath = GetRelativePath(depotPath, connection.Client.Root);
                var clientPath = Path.Combine(connection.Client.Root, relativePath);

                _logger.LogDebug("Deleting file - Depot: {DepotPath}, Client: {ClientPath}", depotPath, clientPath);

                var deletedFiles = connection.Client.DeleteFiles( new DeleteFilesCmdOptions(DeleteFilesCmdFlags.None, changelist.Id), new FileSpec(new LocalPath(clientPath)));

                if (deletedFiles.Count > 0)
                {
                    _logger.LogDebug("Successfully deleted file {DepotPath} from target", depotPath);
                }
                else
                {
                    _logger.LogDebug("Failed to delete file {DepotPath} from target", depotPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception deleting file {DepotPath}", depotPath);
            }
        }


        /// <summary>
        /// Gets filtered files based on filter patterns using server-side filtering
        /// </summary>
        /// <param name="connection">Connection for authenticated operations</param>
        /// <param name="filterPatterns">Filter patterns to apply</param>
        /// <returns>List of filtered files</returns>
        public List<FileMetaData> GetFilteredFiles(Repository repository, List<string> filterPatterns)
        {
            var filteredFiles = new List<FileMetaData>();

            //for each filter pattern, run getFilemetaData and store results
            foreach (var pattern in filterPatterns)
            {
                try
                {
                    var fileMetaDatas = repository.GetFileMetaData(new Options(), new FileSpec(new DepotPath(pattern)));
                    if (fileMetaDatas != null && fileMetaDatas.Count > 0)
                    {
                        filteredFiles.AddRange(fileMetaDatas);
                        _logger.LogDebug("Retrieved {FileCount} files for pattern {Pattern}", fileMetaDatas.Count, pattern);
                    }
                    else
                    {
                        _logger.LogDebug("No files found for pattern {Pattern}", pattern);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting filtered files for pattern {Pattern}", pattern);
                }
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


    }
}
