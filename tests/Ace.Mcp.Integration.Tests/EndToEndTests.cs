using System.Text.Json;
using Ace.Core.Platform;
using Xunit.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ace.Mcp.Integration.Tests;

/// <summary>
/// Serializes the end-to-end test classes: each class spawns its own real
/// Ace.Mcp.Server child process (via <see cref="McpServerFixture"/>) and some
/// mutate their temp repository, so running them one after another keeps the
/// workload deterministic and light on the build agent.
/// </summary>
[CollectionDefinition("MCP end-to-end", DisableParallelization = true)]
public sealed class EndToEndCollection;

/// <summary>
/// End-to-end tests per SRS §23: AI client → MCP → ACE → repository → structured
/// result. Each class gets its own fixture instance (own server process + own temp
/// copy of SampleRepo), so mutating scenarios never touch the shared fixture used
/// by <see cref="McpServerIntegrationTests"/>.
/// </summary>
[Collection("MCP end-to-end")]
public sealed class EndToEndAnalysisTests(McpServerFixture fixture) : IClassFixture<McpServerFixture>
{
    private static readonly string[] RiskLevels = ["Low", "Medium", "High"];

    private const string ChangedFile = "src/Customer.Services/CustomerService.cs";

    [Fact]
    public async Task ImpactAnalyze_EndToEnd_ReturnsComponentsTestsEvidenceAndRisk()
    {
        var json = await CallToolJsonAsync(fixture.Client, "ace_impact_analyze", new Dictionary<string, object?>
        {
            ["repositoryPath"] = fixture.RepositoryPath,
            ["changedFiles"] = new[] { ChangedFile },
        });

        var root = json.RootElement;
        Assert.False(root.TryGetProperty("error", out _), $"unexpected error: {root}");

        // Changed components resolved from the changed file.
        Assert.True(root.GetProperty("changedComponents").GetArrayLength() > 0, "expected changed components");

        // Impact propagates to known consumers across projects/layers.
        var affected = root.GetProperty("affectedComponents")
            .EnumerateArray().Select(element => element.GetString()).ToList();
        Assert.NotEmpty(affected);
        Assert.Contains(affected, component => component!.Contains("OrderService", StringComparison.Ordinal));
        Assert.Contains(affected, component => component!.Contains("CustomerController", StringComparison.Ordinal));

        // Affected tests, evidence trail and a valid merged risk level.
        Assert.True(root.GetProperty("affectedTests").GetArrayLength() > 0, "expected affected tests");
        Assert.True(root.GetProperty("evidence").GetArrayLength() > 0, "expected an evidence trail");
        Assert.Contains(root.GetProperty("riskLevel").GetString(), RiskLevels);
    }

    [Fact]
    public async Task RegressionScope_EndToEnd_RecommendsTestsAndRisk()
    {
        var json = await CallToolJsonAsync(fixture.Client, "ace_regression_scope", new Dictionary<string, object?>
        {
            ["repositoryPath"] = fixture.RepositoryPath,
            ["changedFiles"] = new[] { ChangedFile },
        });

        var root = json.RootElement;
        Assert.False(root.TryGetProperty("error", out _), $"unexpected error: {root}");

        Assert.Contains(root.GetProperty("riskLevel").GetString(), RiskLevels);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("recommendedScope").GetString()));

        // The recommended tests for this change set.
        var recommendedTests = root.GetProperty("affectedTests");
        Assert.True(recommendedTests.GetArrayLength() > 0, "expected recommended tests");
        Assert.All(recommendedTests.EnumerateArray(), test =>
            Assert.False(string.IsNullOrWhiteSpace(test.GetProperty("name").GetString())));
    }

    [Fact]
    public async Task TestsAffected_EndToEnd_ContainsCustomerAndOrderServiceTests()
    {
        var json = await CallToolJsonAsync(fixture.Client, "ace_tests_affected", new Dictionary<string, object?>
        {
            ["repositoryPath"] = fixture.RepositoryPath,
            ["changedFiles"] = new[] { ChangedFile },
        });

        var root = json.RootElement;
        Assert.False(root.TryGetProperty("error", out _), $"unexpected error: {root}");

        var testNames = root.GetProperty("affectedTests")
            .EnumerateArray().Select(test => test.GetProperty("name").GetString()).ToList();

        Assert.Contains(testNames, name => name!.Contains("CustomerServiceTests", StringComparison.Ordinal));
        Assert.Contains(testNames, name => name!.Contains("OrderServiceTests", StringComparison.Ordinal));
    }

    internal static async Task<JsonDocument> CallToolJsonAsync(
        McpClient client, string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        var text = await CallToolTextAsync(client, toolName, arguments);
        return JsonDocument.Parse(text);
    }

    internal static async Task<string> CallToolTextAsync(
        McpClient client, string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: timeout.Token);

        if (result.IsError == true)
        {
            var detail = string.Join(" | ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            Assert.Fail($"Tool {toolName} reported a protocol-level error: {detail}");
        }

        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.False(string.IsNullOrWhiteSpace(block.Text), $"Tool {toolName} returned empty text.");
        return block.Text;
    }
}

/// <summary>
/// Incremental indexing end-to-end (SRS §11/§17): index the repository, modify ONE
/// source file, then connect a SECOND server process to the same repository. The new
/// process must load the persisted index, detect the single changed file, refresh the
/// graph and keep the status consistent — without failing.
/// </summary>
[Collection("MCP end-to-end")]
public sealed class IncrementalIndexingEndToEndTests(McpServerFixture fixture) : IClassFixture<McpServerFixture>
{
    [Fact]
    public async Task ModifyOneFile_SecondServerIncrementallyReindexes_AndGraphReflectsChange()
    {
        // Phase 1 — initial index through the fixture's server.
        var analyze = await EndToEndAnalysisTests.CallToolJsonAsync(fixture.Client, "ace_repository_analyze",
            new Dictionary<string, object?> { ["repositoryPath"] = fixture.RepositoryPath });
        Assert.False(analyze.RootElement.TryGetProperty("error", out _), $"unexpected error: {analyze.RootElement}");

        var statusBefore = await EndToEndAnalysisTests.CallToolJsonAsync(fixture.Client, "ace_status",
            new Dictionary<string, object?> { ["repositoryPath"] = fixture.RepositoryPath });
        var nodeCountBefore = statusBefore.RootElement.GetProperty("nodeCount").GetInt32();
        var fileCountBefore = statusBefore.RootElement.GetProperty("fileCount").GetInt32();
        Assert.True(nodeCountBefore > 0, "expected a non-empty graph after first analyze");
        Assert.False(statusBefore.RootElement.GetProperty("stale").GetBoolean(), "first-time indexing must not report staleness");

        // Phase 2 — modify exactly ONE source file (append a brand-new type).
        var targetFile = Path.Combine(fixture.RepositoryPath, "src", "Customer.Services", "OrderService.cs");
        File.AppendAllText(targetFile, """

            /// <summary>Added by the incremental-indexing end-to-end test.</summary>
            public sealed class OrderAuditTrail
            {
                public string Note { get; set; } = string.Empty;
            }
            """);
        File.SetLastWriteTimeUtc(targetFile, DateTime.UtcNow.AddSeconds(2));

        // Phase 3 — a fresh server process reloads the persisted index incrementally.
        await using var secondClient = await SpawnClientAsync();
        try
        {
            var reanalyze = await EndToEndAnalysisTests.CallToolJsonAsync(secondClient, "ace_repository_analyze",
                new Dictionary<string, object?> { ["repositoryPath"] = fixture.RepositoryPath });
            Assert.False(reanalyze.RootElement.TryGetProperty("error", out _), $"re-analyze failed: {reanalyze.RootElement}");

            var statusAfter = await EndToEndAnalysisTests.CallToolJsonAsync(secondClient, "ace_status",
                new Dictionary<string, object?> { ["repositoryPath"] = fixture.RepositoryPath });
            var root = statusAfter.RootElement;

            // The persisted index existed and exactly one file changed → reported stale+refreshed.
            Assert.True(root.GetProperty("stale").GetBoolean(), "expected the modified file to be detected as stale");
            Assert.Equal(fileCountBefore, root.GetProperty("fileCount").GetInt32());
            Assert.True(root.GetProperty("nodeCount").GetInt32() > nodeCountBefore, "graph must reflect the new type");
            Assert.Empty(root.GetProperty("failedFiles").EnumerateObject());

            // The new symbol is queryable straight from the refreshed graph.
            var search = await EndToEndAnalysisTests.CallToolJsonAsync(secondClient, "ace_code_search",
                new Dictionary<string, object?>
                {
                    ["repositoryPath"] = fixture.RepositoryPath,
                    ["query"] = "OrderAuditTrail",
                });
            Assert.True(search.RootElement.GetArrayLength() > 0, "new type must appear in code search after incremental re-index");
        }
        finally
        {
            // Restore the fixture repository for any later assertions/cleanup.
            var original = await File.ReadAllTextAsync(
                Path.Combine(TestAssets.SampleRepoPath, "src", "Customer.Services", "OrderService.cs"));
            File.WriteAllText(targetFile, original);
        }
    }

    private async Task<McpClient> SpawnClientAsync()
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ace-mcp-server-incremental",
            Command = "dotnet",
            Arguments = [McpServerFixture.ServerDllPath],
        });

        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        return await McpClient.CreateAsync(transport, cancellationToken: connectTimeout.Token);
    }
}

/// <summary>
/// Corrupt-index recovery end-to-end (SRS §17): garbage in .ace/index.json must not
/// error; ACE rebuilds from source and returns a valid structured result.
/// The garbage is written BEFORE the fixture's server ever sees the repository, so the
/// first tool call exercises the load → detect-corruption → full-rebuild path.
/// </summary>
[Collection("MCP end-to-end")]
public sealed class CorruptIndexRecoveryEndToEndTests(McpServerFixture fixture) : IClassFixture<McpServerFixture>
{
    [Fact]
    public async Task CorruptIndex_AnalyzeRecoversWithFullRebuild()
    {
        var aceDirectory = Path.Combine(fixture.RepositoryPath, ".ace");
        Directory.CreateDirectory(aceDirectory);
        var indexPath = Path.Combine(aceDirectory, "index.json");
        File.WriteAllText(indexPath, "%%% this is { definitely ] not json \u0000\u0001 garbage");

        var json = await EndToEndAnalysisTests.CallToolJsonAsync(fixture.Client, "ace_repository_analyze",
            new Dictionary<string, object?> { ["repositoryPath"] = fixture.RepositoryPath });

        var root = json.RootElement;
        Assert.False(root.TryGetProperty("error", out _), $"corrupt index must not surface an error: {root}");
        Assert.True(root.GetProperty("fileCount").GetInt32() > 0, "expected a rebuilt index");
        Assert.Contains(root.GetProperty("languages").EnumerateArray(), element => element.GetString() == "C#");

        var status = await EndToEndAnalysisTests.CallToolJsonAsync(fixture.Client, "ace_status",
            new Dictionary<string, object?> { ["repositoryPath"] = fixture.RepositoryPath });
        Assert.True(status.RootElement.GetProperty("indexed").GetBoolean());
        Assert.True(status.RootElement.GetProperty("nodeCount").GetInt32() > 0, "expected a rebuilt graph");

        // The corrupt artifact was replaced with a valid persisted index.
        using var rebuilt = JsonDocument.Parse(File.ReadAllText(indexPath));
        Assert.False(string.IsNullOrWhiteSpace(rebuilt.RootElement.GetProperty("repository").GetString()));
        Assert.True(rebuilt.RootElement.GetProperty("files").EnumerateObject().Any());
    }
}

/// <summary>
/// Git analysis end-to-end (SRS §13/FR-007): initializes a real git repository in the
/// temp SampleRepo copy, commits a baseline, modifies a file, and drives IGitService
/// working-tree detection into ace_impact_analyze. Skips gracefully when the git CLI
/// is unavailable in the environment.
/// </summary>
[Collection("MCP end-to-end")]
public sealed class GitAnalysisEndToEndTests(McpServerFixture fixture, ITestOutputHelper output) : IClassFixture<McpServerFixture>
{
    private const string ModifiedFile = "src/Customer.Services/CustomerService.cs";

    [Fact]
    public async Task GitWorkingTree_ChangeDetection_FeedsImpactAnalysis()
    {
        var processService = new ProcessService();

        var gitVersion = await processService.RunAsync("git", ["--version"], fixture.RepositoryPath);
        if (!gitVersion.Success)
        {
            output.WriteLine("KNOWN LIMITATION: git CLI is not available in this environment — skipping the git end-to-end path. " +
                             "ACE falls back to explicit changed-file lists (FR-007), which the other end-to-end tests cover.");
            return;
        }

        // Baseline commit in the disposable temp copy (identity passed per-command; no config changes).
        await AssertGitAsync(processService, ["init", "--quiet"]);
        await AssertGitAsync(processService, ["add", "-A"]);
        await AssertGitAsync(processService,
            ["-c", "user.name=ace-tests", "-c", "user.email=ace-tests@example.com", "commit", "--quiet", "-m", "baseline"]);

        // Modify one file after the commit.
        var targetFile = Path.Combine(fixture.RepositoryPath, ModifiedFile.Replace('/', Path.DirectorySeparatorChar));
        File.AppendAllText(targetFile, "\n// modified after baseline commit\n");

        // IGitService working-tree detection (git status --porcelain).
        var gitService = new GitService(processService);
        var status = await gitService.GetStatusAsync(fixture.RepositoryPath);

        Assert.True(status.Available, "git should be reported available");
        Assert.True(status.IsRepository, "the temp copy should be recognized as a git repository");
        Assert.Contains(status.ChangedFiles, file => file.Replace('\\', '/') == ModifiedFile);

        // Feed the git-detected change set through the full MCP impact path.
        var impact = await EndToEndAnalysisTests.CallToolJsonAsync(fixture.Client, "ace_impact_analyze",
            new Dictionary<string, object?>
            {
                ["repositoryPath"] = fixture.RepositoryPath,
                ["changedFiles"] = status.ChangedFiles.ToArray(),
            });

        Assert.False(impact.RootElement.TryGetProperty("error", out _), $"unexpected error: {impact.RootElement}");
        Assert.True(impact.RootElement.GetProperty("changedComponents").GetArrayLength() > 0);
        Assert.True(impact.RootElement.GetProperty("affectedComponents").GetArrayLength() > 0);
        Assert.True(impact.RootElement.GetProperty("affectedTests").GetArrayLength() > 0);
    }

    private async Task AssertGitAsync(ProcessService processService, string[] arguments)
    {
        var result = await processService.RunAsync("git", arguments, fixture.RepositoryPath);
        Assert.True(result.Success, $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
    }
}

/// <summary>Locates the SampleRepo fixture without depending on another test class' privates.</summary>
internal static class TestAssets
{
    public static string SampleRepoPath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ace.sln")))
            {
                directory = directory.Parent;
            }

            var root = directory?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate Ace.sln above the test binary.");
            return Path.Combine(root, "tests", "TestAssets", "SampleRepo");
        }
    }
}
