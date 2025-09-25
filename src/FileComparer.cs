// FileComparer class for comparing files
using System;
using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
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

        // Compare two files by their MD5 hash (fast for performance testing)
        public bool AreFilesIdentical(string filePath1, string filePath2)
        {
            if (!File.Exists(filePath1) || !File.Exists(filePath2))
            {
                _logger.LogWarning("One or both files do not exist: {File1}, {File2}", filePath1, filePath2);
                return false;
            }

            // Compute hashes in parallel for better performance
            var task1 = Task.Run(() => ComputeHash(filePath1));
            var task2 = Task.Run(() => ComputeHash(filePath2));

            Task.WhenAll(task1, task2).Wait();

            var hash1 = task1.Result;
            var hash2 = task2.Result;

            return StructuralComparisons.StructuralEqualityComparer.Equals(hash1, hash2);
        }

        private byte[] ComputeHash(string filePath)
        {
            using (var hashAlgorithm = MD5.Create())
            using (var stream = File.OpenRead(filePath))
            {
                return hashAlgorithm.ComputeHash(stream);
            }
        }
    }
}