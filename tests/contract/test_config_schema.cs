using NJsonSchema;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace P4Sync.Tests.Contract
{
    public class TestConfigSchema
    {
        [Fact]
        public async Task TestConfigAgainstNewSchema()
        {
            // Test the new schema without TargetToSourceFilter
            var projectRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", ".."));
            var schemaPath = Path.Combine(projectRoot, "specs", "main", "contracts", "config.schema.json");
            var configPath = Path.Combine(projectRoot, "src", "test-config-new.json");

            // Create a test config that matches the new schema (without TargetToSourceFilter)
            var testConfig = @"{
  ""SyncProfiles"": [
    {
      ""Name"": ""Test Sync"",
      ""Source"": {
        ""Port"": ""perforce:1666"",
        ""User"": ""testuser"",
        ""Workspace"": ""testworkspace""
      },
      ""Target"": {
        ""Port"": ""perforce:1667"",
        ""User"": ""testuser"",
        ""Workspace"": ""testworkspace""
      },
      ""SyncFilter"": [
        ""//depot/main/...""
      ],
      ""Schedule"": ""0 * * * *""
    }
  ]
}";
            await File.WriteAllTextAsync(configPath, testConfig);

            var schema = await JsonSchema.FromFileAsync(schemaPath);
            var configContent = await File.ReadAllTextAsync(configPath);

            var errors = schema.Validate(configContent);

            Assert.Empty(errors);
        }

        [Fact]
        public async Task TestConfigWithoutTargetToSourceFilterPassesNewSchema()
        {
            // Test that new config without TargetToSourceFilter passes validation
            var projectRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", ".."));
            var schemaPath = Path.Combine(projectRoot, "specs", "main", "contracts", "config.schema.json");

            var validConfig = @"{
  ""SyncProfiles"": [
    {
      ""Name"": ""Test Sync"",
      ""Source"": {
        ""Port"": ""perforce:1666"",
        ""User"": ""testuser"",
        ""Workspace"": ""testworkspace""
      },
      ""Target"": {
        ""Port"": ""perforce:1667"",
        ""User"": ""testuser"",
        ""Workspace"": ""testworkspace""
      },
      ""SyncFilter"": [
        ""//depot/main/...""
      ],
      ""Schedule"": ""0 * * * *""
    }
  ]
}";

            var schema = await JsonSchema.FromFileAsync(schemaPath);
            var errors = schema.Validate(validConfig);

            Assert.Empty(errors);
        }
    }
}
