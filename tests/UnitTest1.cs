using Xunit;
using Perforce.P4;
using Newtonsoft.Json;
using System;
namespace P4Sync.Tests;

public class P4Test
{

    public Repository EstablishConnection()
    {
        Server server = new Server(new ServerAddress("perforce:1666"));
        var mRepository = new Repository(server);
        var mPerforceConnection = mRepository.Connection;

        // use the connection varaibles for this connection
        mPerforceConnection.UserName = "admin";
        mPerforceConnection.Client = new Client();
        mPerforceConnection.Client.Name = "workspace";

        // connect to the server
        mPerforceConnection.Connect(null);
        return mRepository;
    }

    [Fact]
    public void TestConnection()
    {
        var repo = EstablishConnection();
        Assert.True(repo != null && repo.Connection != null && repo.Connection.connectionEstablished());
    }

    [Fact]
    public void TestGetFileMetaData()
    {
        var repo = EstablishConnection();
        var fileMetaDatasOptions = new Options();
        var fileMetaDatas = repo.GetFileMetaData(fileMetaDatasOptions, new FileSpec(new DepotPath("//InternalProjects/SPD2/Source_Files/....fbx")));
        Console.WriteLine($"Found {fileMetaDatas.Count} files");
        foreach (var file in fileMetaDatas)
        {
            Console.WriteLine($"File: {file.DepotPath},File: {file.LocalPath}, HeadRev: {file.HeadRev}, HeadType: {file.HeadType}, HeadAction: {file.HeadAction}");
        }
        // Store fileMetaDatas for later to json file
        System.IO.File.WriteAllText("D:/Projects/P4SyncTransfer/tests/fileMetaDatas.json", JsonConvert.SerializeObject(fileMetaDatas));
        Assert.True(fileMetaDatas != null && fileMetaDatas.Count > 0);
    }

    // Write new file, mark for add, test for changelist containing the file
    [Fact]
    public void TestAddFile()
    {
        var repo = EstablishConnection();
        var client = repo.Connection.Client;
        // Create a new file in the workspace
        var localFilePath = System.IO.Path.Combine(client.Root, "test_add_file.txt");
        System.IO.File.WriteAllText(localFilePath, "This is a test file for add operation.");

        // Mark the file for add
        var fileSpec = new FileSpec(new LocalPath(localFilePath));
        var changelist = repo.CreateChangelist(new Changelist
        {
            Description = "Test add file changelist"
        });
        client.RevertFiles(new Options(), fileSpec); // Ensure the file is not already opened
        var addOptions = new Options(AddFilesCmdFlags.None, changelist.Id, null);
        var addedFiles = client.AddFiles(addOptions, fileSpec);
        Console.WriteLine($"Changelist {changelist.Id} has {addedFiles.Count} opened files.");
        Assert.True(addedFiles != null && addedFiles.Count > 0 );
        var fileMetaData = repo.GetFileMetaData(null, fileSpec);
        Console.WriteLine($"FileMetaData: {JsonConvert.SerializeObject(fileMetaData)}");
        // Cleanup: Revert the changelist and delete the local file
        client.RevertFiles(new Options(RevertFilesCmdFlags.None, changelist.Id), fileSpec);
        if (System.IO.File.Exists(localFilePath))
        {
            System.IO.File.Delete(localFilePath);
        }
    }
}
