using System.Text.Json;
using Ace.Core.Configuration;
using Ace.Core.Models;

namespace Ace.Core.Tests.Configuration;

/// <summary>Tests for AceOptions defaults, ace.json loading and ACE__* environment overrides.</summary>
public class AceOptionsTests : IDisposable
{
    private readonly List<string> _envVarsToClear = [];
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var name in _envVarsToClear)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private void SetEnv(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        _envVarsToClear.Add(name);
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ace-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void Defaults_MatchSpecification()
    {
        var options = new AceOptions();

        Assert.Equal(".ace", options.IndexPath);
        Assert.InRange(options.MaxParallelism, AceOptions.MinParallelism, AceOptions.MaxAllowedParallelism);
        Assert.False(options.EnableGitAnalysis);
        Assert.True(options.EnableArchitectureAnalysis); // SRS §19: enabled by default
        Assert.Contains(".git", options.ExclusionPatterns);
        Assert.Contains("bin", options.ExclusionPatterns);
        Assert.Contains("obj", options.ExclusionPatterns);
        Assert.Contains("node_modules", options.ExclusionPatterns);
        Assert.Contains("packages", options.ExclusionPatterns);
        Assert.Contains(".vscode", options.ExclusionPatterns);
        Assert.Contains(".idea", options.ExclusionPatterns);
        Assert.Contains(".env", options.SensitiveFilePatterns);
        Assert.Contains("*.key", options.SensitiveFilePatterns);
        Assert.Contains("*.pem", options.SensitiveFilePatterns);
        Assert.Contains("secrets.json", options.SensitiveFilePatterns);
        Assert.Contains("credentials.json", options.SensitiveFilePatterns);
        Assert.Empty(options.ArchitectureRules);
    }

    [Fact]
    public void EnvironmentVariables_OverrideDefaults()
    {
        SetEnv("ACE__INDEXPATH", ".custom-ace");
        SetEnv("ACE__MAXPARALLELISM", "3");
        SetEnv("ACE__ENABLEGITANALYSIS", "true");

        var options = AceOptionsFactory.Load();

        Assert.Equal(".custom-ace", options.IndexPath);
        Assert.Equal(3, options.MaxParallelism);
        Assert.True(options.EnableGitAnalysis);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("1000", 64)]
    public void MaxParallelism_IsClampedToOneThroughSixtyFour(string envValue, int expected)
    {
        SetEnv("ACE__MAXPARALLELISM", envValue);

        var options = AceOptionsFactory.Load();

        Assert.Equal(expected, options.MaxParallelism);
    }

    [Fact]
    public void AceJsonInRepositoryRoot_IsLoaded()
    {
        var repo = CreateTempDir();
        var config = new
        {
            ace = new
            {
                indexPath = "custom-index",
                maxParallelism = 2,
                enableGitAnalysis = true,
                enableArchitectureAnalysis = true,
                exclusionPatterns = new[] { "target", "dist" },
                architectureRules = new[]
                {
                    new { name = "layers", layers = new[] { "Controller", "Service", "Repository" } },
                },
            },
        };

        File.WriteAllText(Path.Combine(repo, "ace.json"), JsonSerializer.Serialize(config, AceJson.Options));

        var options = AceOptionsFactory.Load(repo);

        Assert.Equal("custom-index", options.IndexPath);
        Assert.Equal(2, options.MaxParallelism);
        Assert.True(options.EnableGitAnalysis);
        Assert.True(options.EnableArchitectureAnalysis);
        Assert.Equal(new[] { "target", "dist" }, options.ExclusionPatterns);
        var rule = Assert.Single(options.ArchitectureRules);
        Assert.Equal("layers", rule.Name);
        Assert.Equal(new[] { "Controller", "Service", "Repository" }, rule.Layers);
    }

    [Fact]
    public void EnvironmentVariables_WinOverAceJson()
    {
        var repo = CreateTempDir();
        File.WriteAllText(
            Path.Combine(repo, "ace.json"),
            """{ "ace": { "indexPath": "from-file" } }""");
        SetEnv("ACE__INDEXPATH", "from-env");

        var options = AceOptionsFactory.Load(repo);

        Assert.Equal("from-env", options.IndexPath);
    }

    [Fact]
    public void MissingConfigFile_FallsBackToDefaults()
    {
        var repo = CreateTempDir();

        var options = AceOptionsFactory.Load(repo);

        Assert.Equal(".ace", options.IndexPath);
    }

    [Fact]
    public void MalformedAceJson_FallsBackToDefaults()
    {
        var repo = CreateTempDir();
        File.WriteAllText(Path.Combine(repo, "ace.json"), "{ this is not valid json ");

        var options = AceOptionsFactory.Load(repo);

        Assert.Equal(".ace", options.IndexPath);
    }

    [Theory]
    [InlineData("..\\_evil")]
    [InlineData("../_evil")]
    [InlineData("nested\\..\\escape")]
    [InlineData(".")]
    [InlineData(".\\.ace")]
    public void IndexPath_WithTraversalSegments_ResetsToDefault(string maliciousIndexPath)
    {
        var options = new AceOptions { IndexPath = maliciousIndexPath };

        options.Normalize();

        Assert.Equal(".ace", options.IndexPath);
    }

    [Theory]
    [InlineData("C:\\temp\\_evil")]
    [InlineData("\\\\attacker\\share\\ace")]
    public void IndexPath_WhenRooted_ResetsToDefault(string maliciousIndexPath)
    {
        var options = new AceOptions { IndexPath = maliciousIndexPath };

        options.Normalize();

        Assert.Equal(".ace", options.IndexPath);
    }

    [Fact]
    public void IndexPath_PlainRelativeName_IsKept()
    {
        var options = new AceOptions { IndexPath = "custom-index" };

        options.Normalize();

        Assert.Equal("custom-index", options.IndexPath);
    }
}
