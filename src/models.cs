using System.Collections.Generic;
using Perforce.P4;

namespace P4Sync
{

    /// <summary>
    /// Enumeration of sync operations
    /// </summary>
    public enum SyncOperation
    {
        Add,
        Edit,
        Delete,
        Skip
    }

    /// <summary>
    /// Result of syncing a file from source
    /// </summary>
    public class SyncResult
    {
        public SyncOperation Operation { get; set; }
        public string LocalPath { get; set; } = string.Empty;
        public bool Success { get; set; }
        public string ContentHash { get; set; } = string.Empty;
        public string OriginalPath { get; set; } = string.Empty; // For move detection
    }

    // class to hold file metadata information of source / target mapping and revision for history tracking and logging
    public class P4SyncedTransfer
    {
        public string? SourceDepotPath { get; set; }
        public string? SourceLocalPath { get; set; }
        public string? TargetDepotPath { get; set; }
        public string? TargetLocalPath { get; set; }
        public string? SourceAction { get; set; }
        public int SourceRevision { get; set; } = 0;
        public int TargetRevision { get; set; } = 0;
        public SyncOperation TargetOperation { get; set; } = SyncOperation.Skip;
        public string ContentHash { get; set; } = string.Empty;
        public bool Success { get; set; } = false;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public class P4SyncedTransfers
    {
        public DateTime SyncTime { get; set; } = DateTime.Now;
        public List<P4SyncedTransfer> Transfers { get; set; } = new List<P4SyncedTransfer>();
        public int ChangelistNumber { get; set; } = 0;
    }

    // class to hold sync profile information and history of sync operations
    public class SyncHistory
    {
        public string ProfileId { get; set; } = string.Empty;
        public SyncProfile? Profile { get; set; }
        public List<P4SyncedTransfers> Syncs { get; set; } = new List<P4SyncedTransfers>();
    }
}