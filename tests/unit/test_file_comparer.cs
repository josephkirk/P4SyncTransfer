using P4Sync;
using Moq;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;
using Microsoft.Extensions.Logging;

namespace P4Sync.Tests.Unit
{
    public class TestFileComparer : IDisposable
    {
        private readonly Mock<ILogger<P4Operations>> _mockLogger;
        private readonly FileComparer _fileComparer;
        private readonly string _tempDir;

        public TestFileComparer()
        {
            _mockLogger = new Mock<ILogger<P4Operations>>();
            _fileComparer = new FileComparer(_mockLogger.Object);
            _tempDir = Path.Combine(Path.GetTempPath(), "FileComparerTests");
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }

        [Fact]
        public void AreFilesIdentical_IdenticalFiles_ReturnsTrue()
        {
            // Arrange
            var content = "This is test content for identical files.";
            var file1 = Path.Combine(_tempDir, "file1.txt");
            var file2 = Path.Combine(_tempDir, "file2.txt");
            File.WriteAllText(file1, content);
            File.WriteAllText(file2, content);

            // Act
            var result = _fileComparer.AreFilesIdentical(file1, file2);

            // Assert
            Assert.True(result);
        }

        [Fact]
        public void AreFilesIdentical_DifferentFiles_ReturnsFalse()
        {
            // Arrange
            var file1 = Path.Combine(_tempDir, "file1.txt");
            var file2 = Path.Combine(_tempDir, "file2.txt");
            File.WriteAllText(file1, "Content 1");
            File.WriteAllText(file2, "Content 2");

            // Act
            var result = _fileComparer.AreFilesIdentical(file1, file2);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void AreFilesIdentical_FirstFileDoesNotExist_ReturnsFalseAndLogsWarning()
        {
            // Arrange
            var nonExistentFile = Path.Combine(_tempDir, "nonexistent1.txt");
            var existingFile = Path.Combine(_tempDir, "existing.txt");
            File.WriteAllText(existingFile, "content");

            // Act
            var result = _fileComparer.AreFilesIdentical(nonExistentFile, existingFile);

            // Assert
            Assert.False(result);
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString().Contains("One or both files do not exist")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        [Fact]
        public void AreFilesIdentical_SecondFileDoesNotExist_ReturnsFalseAndLogsWarning()
        {
            // Arrange
            var existingFile = Path.Combine(_tempDir, "existing.txt");
            var nonExistentFile = Path.Combine(_tempDir, "nonexistent2.txt");
            File.WriteAllText(existingFile, "content");

            // Act
            var result = _fileComparer.AreFilesIdentical(existingFile, nonExistentFile);

            // Assert
            Assert.False(result);
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString().Contains("One or both files do not exist")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        [Fact]
        public void AreFilesIdentical_BothFilesDoNotExist_ReturnsFalseAndLogsWarning()
        {
            // Arrange
            var nonExistentFile1 = Path.Combine(_tempDir, "nonexistent1.txt");
            var nonExistentFile2 = Path.Combine(_tempDir, "nonexistent2.txt");

            // Act
            var result = _fileComparer.AreFilesIdentical(nonExistentFile1, nonExistentFile2);

            // Assert
            Assert.False(result);
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString().Contains("One or both files do not exist")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception, string>>()),
                Times.Once);
        }

        [Fact]
        public void AreFilesIdentical_PerformanceTest_LargeFiles()
        {
            // Test O(n) complexity by measuring time for different file sizes
            var sizes = new[] { 256L * 1024 * 1024, 512L * 1024 * 1024, 1024L * 1024 * 1024 }; // 256MB, 512MB, 1GB
            var times = new List<long>();

            foreach (var size in sizes)
            {
                // Arrange - Create large files by writing in chunks to avoid OOM
                var file1 = Path.Combine(_tempDir, $"large1_{size}.txt");
                var file2 = Path.Combine(_tempDir, $"large2_{size}.txt");
                CreateLargeFile(file1, size);
                CreateLargeFile(file2, size);

                // Act
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var result = _fileComparer.AreFilesIdentical(file1, file2);
                stopwatch.Stop();

                // Assert result
                Assert.True(result);

                times.Add(stopwatch.ElapsedMilliseconds);

                // Clean up files immediately to save space
                File.Delete(file1);
                File.Delete(file2);
            }

            // Verify O(n) complexity: time should roughly double when size doubles
            var ratio1 = (double)times[1] / times[0]; // 512MB / 256MB
            var ratio2 = (double)times[2] / times[1]; // 1GB / 512MB

            // Allow some tolerance for system variability (1.5x to 3x is reasonable for linear)
            Assert.True(ratio1 >= 1.5 && ratio1 <= 3.0, $"Time ratio 512MB/256MB: {ratio1:F2}, expected ~2.0");
            Assert.True(ratio2 >= 1.5 && ratio2 <= 3.0, $"Time ratio 1GB/512MB: {ratio2:F2}, expected ~2.0");

            // Overall: 1GB comparison should complete in reasonable time (under 60 seconds)
            Assert.True(times[2] < 60000, $"1GB comparison took {times[2]}ms, expected < 60000ms");
        }

        private void CreateLargeFile(string filePath, long size)
        {
            const int chunkSize = 1024 * 1024; // 1MB chunks
            var chunk = new string('A', chunkSize);
            using (var writer = new StreamWriter(filePath, false))
            {
                long written = 0;
                while (written < size)
                {
                    var toWrite = Math.Min(chunkSize, (int)(size - written));
                    writer.Write(chunk.Substring(0, toWrite));
                    written += toWrite;
                }
            }
        }
    }
}