// FileComparer class for comparing files
using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace P4Sync
{
    public class FileComparer
    {
        private readonly ILogger<P4Sync.P4Operations> _logger;

        public FileComparer(ILogger<P4Sync.P4Operations> logger)
        {
            _logger = logger;
        }

        // Compare two files by their SHA256 hash
        public bool AreFilesIdentical(string filePath1, string filePath2)
        {
            if (!File.Exists(filePath1) || !File.Exists(filePath2))
            {
                _logger.LogWarning("One or both files do not exist: {File1}, {File2}", filePath1, filePath2);
                return false;
            }

            using (var hashAlgorithm = SHA256.Create())
            {
                using (var stream1 = File.OpenRead(filePath1))
                using (var stream2 = File.OpenRead(filePath2))
                {
                    var hash1 = hashAlgorithm.ComputeHash(stream1);
                    var hash2 = hashAlgorithm.ComputeHash(stream2);

                    return StructuralComparisons.StructuralEqualityComparer.Equals(hash1, hash2);
                }
            }
        }
    }
}