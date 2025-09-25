using System.Collections.Generic;

namespace P4Sync
{
    public class P4Connection
    {
        public string? Port { get; set; }
        public string? User { get; set; }
        public string? Workspace { get; set; }
    }

    public class SyncProfile
    {
        public string? Name { get; set; }
        public P4Connection? Source { get; set; }
        public P4Connection? Target { get; set; }
        public List<string>? SyncFilter { get; set; }
        public string? Schedule { get; set; }
        public bool AutoSubmit { get; set; } = false; // Whether to automatically submit changelists after sync operations
        public string? Description { get; set; } // Description for changelists created during sync
    }

    public class AppConfig
    {
        public string[]? Filters { get; set; }
        public List<SyncProfile>? SyncProfiles { get; set; }
        public LoggingConfiguration? Logging { get; set; }
    }
}
