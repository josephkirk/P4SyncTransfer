using System.Collections.Generic;

namespace P4Sync
{
    /// <summary>
    /// Configuration for Microsoft.Extensions.Logging
    /// </summary>
    public class LoggingConfiguration
    {
        /// <summary>
        /// Log level settings for different categories
        /// </summary>
        public LogLevelConfiguration? LogLevel { get; set; }

        /// <summary>
        /// Console logging configuration
        /// </summary>
        public ConsoleConfiguration? Console { get; set; }

        /// <summary>
        /// File logging configuration
        /// </summary>
        public FileConfiguration? File { get; set; }
    }

    /// <summary>
    /// Configuration for log levels
    /// </summary>
    public class LogLevelConfiguration
    {
        /// <summary>
        /// Default log level for all categories
        /// </summary>
        public string? Default { get; set; }

        /// <summary>
        /// Category-specific log level overrides
        /// </summary>
        public Dictionary<string, string>? Overrides { get; set; }
    }

    /// <summary>
    /// Configuration for console logging
    /// </summary>
    public class ConsoleConfiguration
    {
        /// <summary>
        /// Name of the console formatter ("simple", "json", "systemd")
        /// </summary>
        public string? FormatterName { get; set; }

        /// <summary>
        /// Whether to include logging scopes in output
        /// </summary>
        public bool IncludeScopes { get; set; }

        /// <summary>
        /// Timestamp format for console logs
        /// </summary>
        public string? TimestampFormat { get; set; }
    }

    /// <summary>
    /// Configuration for file logging
    /// </summary>
    public class FileConfiguration
    {
        /// <summary>
        /// Base path for log files
        /// </summary>
        public string? Path { get; set; }

        /// <summary>
        /// Name of the file formatter ("json", "simple")
        /// </summary>
        public string? FormatterName { get; set; }

        /// <summary>
        /// Maximum size per log file in bytes
        /// </summary>
        public long MaxFileSize { get; set; }

        /// <summary>
        /// Maximum number of log files to keep
        /// </summary>
        public int MaxFiles { get; set; }

        /// <summary>
        /// Whether to include logging scopes in file output
        /// </summary>
        public bool IncludeScopes { get; set; }
    }
}