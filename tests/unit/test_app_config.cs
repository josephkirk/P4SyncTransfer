using P4Sync;
using System.Collections.Generic;
using Xunit;

namespace P4Sync.Tests.Unit
{
    public class TestAppConfig
    {
        [Fact]
        public void SyncProfile_CanCreateWithSyncFilter()
        {
            // Test that SyncProfile can be created with SyncFilter
            var profile = new SyncProfile
            {
                Name = "TestProfile",
                Source = new P4Connection
                {
                    Port = "localhost:1666",
                    User = "testuser",
                    Workspace = "testworkspace"
                },
                Target = new P4Connection
                {
                    Port = "localhost:1667",
                    User = "testuser",
                    Workspace = "testworkspace"
                },
                SyncFilter = new List<string> { "*.txt", "*.md" },
                Schedule = "0 * * * *"
            };

            Assert.NotNull(profile);
            Assert.Equal("TestProfile", profile.Name);
            Assert.NotNull(profile.Source);
            Assert.NotNull(profile.Target);
            Assert.NotNull(profile.SyncFilter);
            Assert.Equal(2, profile.SyncFilter.Count);
            Assert.Equal("0 * * * *", profile.Schedule);
        }

        [Fact]
        public void SyncProfile_SyncFilterOnly()
        {
            // Test that SyncFilter works correctly
            var profile = new SyncProfile
            {
                Name = "SyncProfile",
                SyncFilter = new List<string> { "//depot/src/..." }
            };

            Assert.NotNull(profile.SyncFilter);
            Assert.Single(profile.SyncFilter);
            Assert.Equal("//depot/src/...", profile.SyncFilter[0]);
        }

        [Fact]
        public void AppConfig_WithMultipleProfiles()
        {
            // Test AppConfig with multiple unidirectional sync profiles
            var appConfig = new AppConfig
            {
                SyncProfiles = new List<SyncProfile>
                {
                    new SyncProfile
                    {
                        Name = "SourceToTargetSync1",
                        SyncFilter = new List<string> { "//depot/main/..." }
                    },
                    new SyncProfile
                    {
                        Name = "SourceToTargetSync2",
                        SyncFilter = new List<string> { "//depot/main/..." }
                    }
                }
            };

            Assert.NotNull(appConfig.SyncProfiles);
            Assert.Equal(2, appConfig.SyncProfiles.Count);
            Assert.Equal("SourceToTargetSync1", appConfig.SyncProfiles[0].Name);
            Assert.Equal("SourceToTargetSync2", appConfig.SyncProfiles[1].Name);
        }
    }
}