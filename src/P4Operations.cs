using Perforce.P4;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
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
        private readonly P4SyncHistory _syncHistory;

        /// <summary>
        /// Constructor with dependency injection
        /// </summary>
        /// <param name="logger">Logger instance</param>
        /// <param name="syncHistory">Sync history instance</param>
        public P4Operations(ILogger<P4Operations> logger, P4SyncHistory syncHistory)
        {
            _logger = logger;
            _syncHistory = syncHistory;
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

            _logger.LogInformation("Executing sync for profile: {ProfileName}, source port: {SourcePort}, target port: {TargetPort}", profile.Name, profile.Source.Port, profile.Target.Port);

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
                    ExecuteDirectionalSync(sourceRepo, targetRepo, sourceConnection, targetConnection, sourceClient, targetClient, profile, profile.SyncFilter);
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
            Client fromClient, Client toClient, SyncProfile profile, List<string> filterPatterns)
        {
            try
            {
                var syncTransfersRecord = new P4SyncedTransfers();

                _logger.LogDebug("ExecuteDirectionalSync started");
                _logger.LogDebug("fromRepo: {Status}", fromRepo != null ? "OK" : "NULL");
                _logger.LogDebug("toRepo: {Status}", toRepo != null ? "OK" : "NULL");
                _logger.LogDebug("fromClient: {Status}", fromClient != null ? "OK" : "NULL");
                _logger.LogDebug("toClient: {Status}", toClient != null ? "OK" : "NULL");

                // Get workspace information for path translation
                _logger.LogDebug("Getting workspace information for path translation");
                var sourceWorkspace = new WorkspaceInfo();
                var targetWorkspace = new WorkspaceInfo();


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

                // We get all files we need to process using GetFileMetaData with the filter patterns
                _logger.LogDebug("Getting source files");
                var sourceFiles = GetFilteredFiles(fromRepo, filterPatterns);
                
                // for each source file, convert them to relative path using source client root then resolve them to absolute path using target client root
                foreach (var sourceFile in sourceFiles)
                {
                    _logger.LogDebug("Processing source file {SourceFile}", sourceFile.DepotPath.Path);
                    // _logger.LogDebug("Processing source file {SourceFile}", JsonSerializer.Serialize(sourceFile));
                    var sourceLocalPath = "";
                    if (sourceFile.LocalPath != null)
                    {
                        sourceLocalPath = sourceFile.LocalPath.Path;
                    }
                    if (string.IsNullOrEmpty(sourceLocalPath))
                    {
                        var resolveFileSpec = fromConnection.Client.GetClientFileMappings([new DepotPath(sourceFile.DepotPath.Path)]).FirstOrDefault();
                        if (resolveFileSpec != null && resolveFileSpec.LocalPath != null)
                        {
                            sourceLocalPath = resolveFileSpec.LocalPath.Path;
                        }
                    }
                    if (string.IsNullOrEmpty(sourceLocalPath))
                    {
                        _logger.LogDebug("Source file {SourceFile} does not have a valid local path, skipping", sourceFile.DepotPath.Path);
                        continue;
                    }
                    var lastSyncForSource = _syncHistory.QueryTransfers(t => t.SourceDepotPath == sourceFile.DepotPath.Path && t.SourceRevision == sourceFile.HeadRev && t.Success).FirstOrDefault();
                    if (lastSyncForSource != null)
                    {
                        _logger.LogDebug("File {SourceFile} at revision {SourceRev} was already synced in last sync, skipping", sourceFile.DepotPath.Path, sourceFile.HeadRev);
                        continue;
                    }
                    // Convert source local path to relative path using source client root
                    var sourceRelativePath = Path.GetRelativePath(fromClient.Root, sourceLocalPath);

                    // Resolve relative path to target absolute path using target client root
                    var targetAbsolutePath = Path.Combine(toClient.Root, sourceRelativePath);
                    _logger.LogDebug("Mapped source file {SourcePath} to target path {TargetPath}", sourceFile.DepotPath.Path, targetAbsolutePath);
                    _logger.LogDebug("Source File Digest: {SourceDigest}", sourceFile.Digest);

                    // use GetFileMetaData to resolve target local path to depot path
                    var targetFileSpec = toConnection.Client.GetClientFileMappings([new LocalPath(targetAbsolutePath)]).FirstOrDefault();
                    if (targetFileSpec == null || targetFileSpec.DepotPath == null)
                    {
                        _logger.LogDebug("Target file {TargetPath} does not exist in depot, will be added", targetAbsolutePath);
                        throw new FileNotFoundException("Target file not able to resolved on target depot", targetAbsolutePath);
                    }
                    var targetDepotPath = targetFileSpec.DepotPath.Path;
                    var targetFileSpecs = toRepo.GetFileMetaData(new Options(), new FileSpec(new DepotPath(targetDepotPath)));
                    int expectedTargetHeadRev = 1;
                    bool isTargetExistOnDepot = false;

                    // Check if target file exists on target depot
                    if (targetFileSpecs != null && targetFileSpecs.Count > 0)
                    {
                        var resolvedTargetFileSpec = targetFileSpecs[0];
                        isTargetExistOnDepot = resolvedTargetFileSpec.HeadRev > 0 || resolvedTargetFileSpec.HeadAction != FileAction.Delete || resolvedTargetFileSpec.HeadAction != FileAction.MoveDelete;
                        expectedTargetHeadRev = resolvedTargetFileSpec.HeadRev + 1;
                    }
                    _logger.LogDebug("Resolved target file {TargetPath} exists on target depot: {Exists}", targetDepotPath, isTargetExistOnDepot);

                    // Determine sync operation based on source file action and target existence
                    FileAction sourceHeadAction = sourceFile.HeadAction;
                    SyncOperation syncOperation = sourceHeadAction switch
                    {
                        FileAction.Delete or FileAction.MoveDelete => isTargetExistOnDepot ? SyncOperation.Delete : SyncOperation.Skip,
                        _ => isTargetExistOnDepot ? SyncOperation.Edit : SyncOperation.Add
                    };

                    // Create sync transfer record
                    var syncTransferRecord = new P4SyncedTransfer
                    {
                        SourceDepotPath = sourceFile.DepotPath.Path,
                        SourceLocalPath = sourceLocalPath,
                        TargetDepotPath = targetDepotPath,
                        TargetLocalPath = targetAbsolutePath,
                        SourceRevision = sourceFile.HeadRev,
                        TargetRevision = expectedTargetHeadRev,
                        SourceAction = sourceHeadAction.ToString(),
                        TargetOperation = syncOperation,
                        ContentHash = sourceFile.Digest ?? string.Empty,
                    };

                    bool success = ApplyP4ActionToTarget(fromConnection, toConnection, sourceFile.DepotPath.Path, sourceLocalPath, targetDepotPath, targetAbsolutePath, syncOperation, changelist);

                    if (!success)
                    {
                        syncTransferRecord.ErrorMessage = $"Failed to apply action {syncOperation} for file {sourceFile.DepotPath.Path} to target {targetDepotPath}";
                        _logger.LogDebug("Failed to apply action {Action} for file {SourcePath} to target {TargetPath}", syncOperation, sourceFile.DepotPath.Path, targetDepotPath);
                    }
                    syncTransferRecord.Success = success;
                    syncTransfersRecord.Transfers.Add(syncTransferRecord);

                }
                syncTransfersRecord.ChangelistNumber = changelist != null ? changelist.Id : 0;
                // Submit the changelist if any files were modified and auto-submit is enabled
                if (toRepo != null && changelist != null && profile.AutoSubmit)
                {
                    SubmitOrDeleteChangelist(toRepo, changelist, syncTransfersRecord.Transfers.Count != 0);
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing sync for profile {ProfileName}", profile.Name);
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
                _logger.LogDebug("Syncing file {DepotPath} to client {ClientName}", depotPath, connection.Client.Name);
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
                return fileSpecs[0].HeadRev > 0 || fileSpecs[0].HeadAction != FileAction.Delete || fileSpecs[0].HeadAction != FileAction.MoveDelete;
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
        private bool ApplyP4ActionToTarget(Connection fromConnection, Connection toConnection, string sourceDepotPath, string sourceLocalPath, string targetDepotPath, string targetLocalPath, SyncOperation syncOperation, Changelist? changelist)
        {
            if (syncOperation == SyncOperation.Skip)
            {
                _logger.LogDebug("Skipping file {SourceDepotPath} as sync operation is Skip", sourceDepotPath);
                return true;
            }
            bool isSuccess = false;

            if (fromConnection == null || toConnection == null)
            {
                _logger.LogDebug("Cannot add file - source or target connection is null");
                return false;
            }
            if (fromConnection.connectionEstablished() == false)
            {
                fromConnection.Connect(null);
            }
            if (toConnection.connectionEstablished() == false)
            {
                toConnection.Connect(null);
            }
            if (fromConnection.connectionEstablished() == false || toConnection.connectionEstablished() == false)
            {
                _logger.LogDebug("Cannot add file - source or target connection is not established");
                return false;
            }
            try
            {
                _logger.LogDebug("Operating on file {SourceDepotPath} to {TargetDepotPath} with action {SyncOperation}", sourceDepotPath, targetDepotPath, syncOperation);
                if (syncOperation == SyncOperation.Delete)
                {
                    SyncFileToClient(toConnection, targetDepotPath, SyncFilesCmdFlags.Force);

                    // Delete the file from target
                    var deletedFiles = toConnection.Client.DeleteFiles(new DeleteFilesCmdOptions(DeleteFilesCmdFlags.None, changelist.Id), new FileSpec(new LocalPath(targetLocalPath)));

                    if (deletedFiles.Count > 0)
                    {
                        _logger.LogDebug("Successfully deleted file {DepotPath} from target", targetDepotPath);
                    }
                    else
                    {
                        _logger.LogDebug("Failed to delete file {DepotPath} from target", targetDepotPath);
                    }
                    return true;
                }

                
                // Sync the source file to source client workspace
                SyncFileToClient(fromConnection, sourceDepotPath);


                if (!System.IO.File.Exists(sourceLocalPath))
                {
                    throw new FileNotFoundException("Source file not found in source client workspace", sourceLocalPath);
                }
                // Ensure target directory exists
                var directory = Path.GetDirectoryName(targetLocalPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogDebug("Created directory: {Directory}", directory);
                }

                // Check if target file already exists and handle it
                if (System.IO.File.Exists(targetLocalPath))
                {
                    var fileInfo = new FileInfo(targetLocalPath);
                    if (fileInfo.IsReadOnly)
                    {
                        // File is read-only (likely under Perforce control), make it writable
                        fileInfo.IsReadOnly = false;
                        _logger.LogDebug("Made read-only file writable: {TargetPath}", targetLocalPath);
                    }
                }


                // Copy file from source client to target client
                if (sourceLocalPath != targetLocalPath)
                {
                    _logger.LogDebug("Copying file - Source: {SourcePath}, Target: {TargetPath}", sourceLocalPath, targetLocalPath);
                    System.IO.File.Copy(sourceLocalPath, targetLocalPath, true);
                    _logger.LogDebug("Copied file from {Source} to {Target}", sourceLocalPath, targetLocalPath);
                }
                switch (syncOperation)
                {
                    case SyncOperation.Add:
                        var addfiles = toConnection.Client.AddFiles(new Options(AddFilesCmdFlags.None, changelist.Id, null), new FileSpec(new LocalPath(targetLocalPath)));

                        if (addfiles.Count > 0)
                        {
                            _logger.LogDebug("Successfully added file {TargetPath} to target", targetDepotPath);
                            isSuccess = true;
                        }
                        else
                        {
                            _logger.LogDebug("Failed to add file {TargetPath} to target", targetDepotPath);
                            isSuccess = false;
                        }
                        break;
                    case SyncOperation.Edit:
                        SyncFileToClient(toConnection, targetDepotPath, SyncFilesCmdFlags.ServerOnly);
                        var editfiles = toConnection.Client.EditFiles(new Options(EditFilesCmdFlags.None, changelist.Id, null), new FileSpec(new LocalPath(targetLocalPath)));
                        if (editfiles.Count > 0)
                        {
                            _logger.LogDebug("Successfully opened file {TargetPath} for edit on target", targetDepotPath);
                            isSuccess = true;
                        }
                        else
                        {
                            isSuccess = false;
                            _logger.LogDebug("Failed to open file {TargetPath} for edit on target", targetDepotPath);
                        }
                        
                        break;
                    default:
                        _logger.LogDebug("Unhandled source head action {Action} for file {SourcePath}", syncOperation, sourceDepotPath);
                        break;
                }

            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Exception adding file {TargetPath}", targetDepotPath);
                _logger.LogDebug("Stack trace: {StackTrace}", ex.StackTrace);
            }
            return isSuccess;
        }

        /// <summary>
        /// Converts a Perforce FileAction to a SyncOperation
        /// </summary>
        /// <param name="fileAction">The FileAction from Perforce</param>
        /// <returns>The corresponding SyncOperation</returns>
        private SyncOperation ConvertFileActionToSyncOperation(FileAction fileAction)
        {
            return fileAction switch
            {
                FileAction.Add or FileAction.MoveAdd => SyncOperation.Add,
                FileAction.Edit or FileAction.Integrate => SyncOperation.Edit,
                FileAction.Delete or FileAction.MoveDelete => SyncOperation.Delete,
                _ => SyncOperation.Skip
            };
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
                    var fileMetaDataOptions = new GetFileMetaDataCmdOptions(GetFileMetadataCmdFlags.None , null, null, 0, null, null);
                    var fileMetaDatas = repository.GetFileMetaData(fileMetaDataOptions, FileSpec.DepotSpec(pattern));
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
        public void SubmitOrDeleteChangelist(Repository repo, Changelist changelist, bool shouldDeleteChangelist)
        {
            try
            {
                var changelistInfo = repo.GetChangelist(changelist.Id);
                bool isChangelistEmpty = changelistInfo.Files == null || changelistInfo.Files.Count == 0;
                if (!isChangelistEmpty)
                {
                    // Try submitting with the changelist's built-in submit method
                    ClientSubmitOptions clientOptions = new ClientSubmitOptions(false, SubmitType.RevertUnchanged);
                    SubmitCmdOptions options = new SubmitCmdOptions(SubmitFilesCmdFlags.None,
                    changelist.Id, null, changelist.Description, clientOptions);
                    changelist.Submit(options);
                    _logger.LogInformation("Submit completed successfully with Changelist {ChangelistId} contains {FileCount} files.", changelist.Id, changelistInfo.Files.Count);
                    return;
                }
                else
                {
                    _logger.LogInformation("No files in changelist {ChangelistId}.", changelist.Id);

                }
                if (shouldDeleteChangelist || isChangelistEmpty)
                {
                    // Delete the empty changelist
                    repo.DeleteChangelist(changelistInfo,null);
                    _logger.LogDebug("Deleted empty changelist {ChangelistId}", changelist.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error submitting changelist {ChangelistId}", changelist.Id);
            }
        }


    }
}
