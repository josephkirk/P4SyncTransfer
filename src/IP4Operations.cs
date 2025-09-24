using System.Collections.Generic;
using Perforce.P4;

namespace P4Sync
{
    /// <summary>
    /// Interface for Perforce operations that can be implemented using different approaches
    /// (P4 .NET API or external p4.exe process calls)
    /// </summary>
    public interface IP4Operations
    {
        /// <summary>
        /// Executes synchronization from source to target repository based on profile configuration
        /// </summary>
        /// <param name="profile">Sync profile configuration containing source, target, and filter information</param>
        void ExecuteSync(SyncProfile profile);

        /// <summary>
        /// Gets filtered files based on filter patterns
        /// </summary>
        /// <param name="connection">Connection for authenticated operations</param>
        /// <param name="filterPatterns">Filter patterns to apply</param>
        /// <returns>List of filtered files</returns>
        List<FileMetaData> GetFilteredFiles(Repository repository, List<string> filterPatterns);
    }
}