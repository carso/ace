using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ace.Mcp.Integration.Tests;

/// <summary>
/// Locates the solution, the SampleRepo fixture and the built server assembly, then
/// spawns the REAL Ace.Mcp.Server as a child process and connects an SDK McpClient
/// over stdio. One server instance is shared by all tests in the fixture; the
/// repository analyzed is a throw-away copy of SampleRepo so .ace output never
/// pollutes the fixture itself.
/// </summary>
public sealed class McpServerFixture : IAsyncLifetime
{
    private string? _tempRepository;

    /// <summary>Temp copy of SampleRepo used for all tool calls.</summary>
    public string RepositoryPath { get; private set; } = string.Empty;

    public McpClient Client { get; private set; } = null!;

    public static string ServerDllPath => Path.Combine(AppContext.BaseDirectory, "Ace.Mcp.Server.dll");

    public async Task InitializeAsync()
    {
        Assert.True(File.Exists(ServerDllPath), $"Server binary not found: {ServerDllPath}");

        _tempRepository = CreateTempCopyOfSampleRepo();
        RepositoryPath = _tempRepository;

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ace-mcp-server",
            Command = "dotnet",
            Arguments = [ServerDllPath],
        });

        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        Client = await McpClient.CreateAsync(transport, cancellationToken: connectTimeout.Token);
    }

    public async Task DisposeAsync()
    {
        if (Client is not null)
        {
            await Client.DisposeAsync();
        }

        // Give the server a moment to complete its graceful shutdown (stdio EOF →
        // host stop) so it releases the temp repository before deletion.
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        if (_tempRepository is not null)
        {
            // Windows may briefly hold handles (file indexer/AV) after process exit;
            // retry briefly instead of leaking temp dirs.
            for (var attempt = 0; attempt < 6; attempt++)
            {
                if (TryDeleteDirectory(_tempRepository))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    private static string CreateTempCopyOfSampleRepo()
    {
        // Best-effort sweep of residue from previous interrupted/crashed runs. Only
        // directories older than the current run could be orphaned — fresh ones may
        // belong to fixtures initializing in parallel and must not be touched.
        var stagingRoot = Path.Combine(Path.GetTempPath(), "ace-mcp-tests");
        if (Directory.Exists(stagingRoot))
        {
            var cutoff = DateTime.Now.AddMinutes(-10);
            foreach (var leftover in Directory.EnumerateDirectories(stagingRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTime(leftover) < cutoff)
                    {
                        TryDeleteDirectory(leftover);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or already gone — leave it alone.
                }
            }
        }

        var sampleRepo = Path.Combine(FindRepoRoot(), "tests", "TestAssets", "SampleRepo");
        Assert.True(Directory.Exists(sampleRepo), $"SampleRepo fixture not found: {sampleRepo}");

        var temp = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        CopyDirectory(sampleRepo, temp);
        return temp;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ace.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate Ace.sln above the test binary.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var subDirectory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(subDirectory, Path.Combine(destination, Path.GetFileName(subDirectory)));
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            // Git marks object files read-only; Directory.Delete refuses those.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Attribute clear is best-effort; deletion may still succeed.
                }
            }

            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A handle still held by an exiting process; the caller may retry.
            return false;
        }
    }
}

/// <summary>
/// End-to-end MCP integration tests (SRS §23): real server process over stdio,
/// SDK client, tool discovery and structured tool results (§8, §9, §17, §21).
/// </summary>
public sealed class McpServerIntegrationTests(McpServerFixture fixture) : IClassFixture<McpServerFixture>
{
    private static readonly string[] ExpectedTools =
    [
        "ace_repository_analyze",
        "ace_context_get",
        "ace_code_search",
        "ace_dependencies_get",
        "ace_graph_build",
        "ace_graph_query",
        "ace_impact_analyze",
        "ace_risk_analyze",
        "ace_tests_affected",
        "ace_regression_scope",
        "ace_architecture_analyze",
        "ace_status",
    ];

    [Fact]
    public async Task ListTools_ReturnsExactlyTheTwelveAceTools()
    {
        var tools = await CallWithTimeoutAsync(ct => fixture.Client.ListToolsAsync(cancellationToken: ct));

        var names = tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(ExpectedTools.OrderBy(name => name, StringComparer.Ordinal), names);
    }

    [Fact]
    public async Task RepositoryAnalyze_ReturnsStructuredContext()
    {
        var json = await CallToolJsonAsync("ace_repository_analyze", new Dictionary<string, object?>
        {
            ["repositoryPath"] = fixture.RepositoryPath,
        });

        Assert.True(json.RootElement.TryGetProperty("fileCount", out var fileCount), "missing fileCount");
        Assert.True(fileCount.GetInt32() > 0, "expected at least one discovered file");

        Assert.True(json.RootElement.TryGetProperty("languages", out var languages), "missing languages");
        Assert.True(languages.GetArrayLength() > 0, "expected at least one detected language");
        Assert.Contains(languages.EnumerateArray(), element => element.GetString() == "C#");

        Assert.True(json.RootElement.TryGetProperty("testProjects", out var testProjects), "missing testProjects");
        Assert.True(testProjects.GetArrayLength() > 0, "expected at least one test project");
    }

    [Fact]
    public async Task Status_ExposesApiVersionAndIndexStats()
    {
        var json = await CallToolJsonAsync("ace_status", new Dictionary<string, object?>
        {
            ["repositoryPath"] = fixture.RepositoryPath,
        });

        Assert.Equal("ACE MCP v1", json.RootElement.GetProperty("apiVersion").GetString());
        Assert.True(json.RootElement.GetProperty("indexed").GetBoolean());
        Assert.True(json.RootElement.GetProperty("nodeCount").GetInt32() > 0, "expected a non-empty graph");
        Assert.True(json.RootElement.GetProperty("edgeCount").GetInt32() > 0, "expected graph edges");
    }

    [Fact]
    public async Task ImpactAnalyze_ReturnsAffectedComponentsTestsAndEvidence()
    {
        var json = await CallToolJsonAsync("ace_impact_analyze", new Dictionary<string, object?>
        {
            ["repositoryPath"] = fixture.RepositoryPath,
            ["changedFiles"] = new[] { "src/Customer.Services/CustomerService.cs" },
        });

        var root = json.RootElement;
        Assert.False(root.TryGetProperty("error", out _), $"unexpected error: {root}");

        Assert.True(root.TryGetProperty("riskLevel", out var riskLevel), "missing riskLevel");
        Assert.False(string.IsNullOrWhiteSpace(riskLevel.GetString()));

        Assert.True(root.GetProperty("affectedComponents").GetArrayLength() > 0, "expected affected components");
        Assert.True(root.GetProperty("affectedTests").GetArrayLength() > 0, "expected affected tests");
        Assert.True(root.GetProperty("evidence").GetArrayLength() > 0, "expected an evidence trail");

        var evidenceLink = root.GetProperty("evidence")[0];
        Assert.True(evidenceLink.TryGetProperty("source", out _), "evidence link missing source");
        Assert.True(evidenceLink.TryGetProperty("relationship", out _), "evidence link missing relationship");
        Assert.True(evidenceLink.TryGetProperty("target", out _), "evidence link missing target");
    }

    [Fact]
    public async Task ImpactAnalyze_PathTraversal_IsRejectedWithoutCrashing()
    {
        var text = await CallToolTextAsync("ace_impact_analyze", new Dictionary<string, object?>
        {
            ["repositoryPath"] = fixture.RepositoryPath,
            ["changedFiles"] = new[] { @"..\..\Windows\System32\evil.cs" },
        });

        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            // Structured rejection (preferred): a path_security error, never a crash.
            Assert.Equal("path_security", error.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        }
        else
        {
            // Tolerated alternative: the escaping file simply produced no impact.
            Assert.Equal(0, root.GetProperty("affectedComponents").GetArrayLength());
            Assert.Equal(0, root.GetProperty("affectedTests").GetArrayLength());
        }

        // The server must still be alive and serving requests afterwards.
        var status = await CallToolJsonAsync("ace_status", new Dictionary<string, object?>
        {
            ["repositoryPath"] = fixture.RepositoryPath,
        });
        Assert.Equal("ACE MCP v1", status.RootElement.GetProperty("apiVersion").GetString());
    }

    [Fact]
    public async Task RelativeRepositoryPath_IsRejectedAsInvalidArgument()
    {
        // A relative root must never be resolved against the server process CWD;
        // it is rejected with a structured invalid_argument error.
        using var json = await CallToolJsonAsync("ace_status", new Dictionary<string, object?>
        {
            ["repositoryPath"] = "relative/repo/path",
        });

        var root = json.RootElement;
        Assert.True(root.TryGetProperty("error", out var error), "expected a structured error");
        Assert.Equal("invalid_argument", error.GetProperty("code").GetString());
        Assert.Contains("absolute", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GitWorkingTreeInput_RefusedWhenGitAnalysisDisabled()
    {
        // The fixture repository has no ace.json, so enableGitAnalysis defaults to
        // false and git-based change inputs must be refused with a structured error.
        using var json = await CallToolJsonAsync("ace_impact_analyze", new Dictionary<string, object?>
        {
            ["repositoryPath"] = fixture.RepositoryPath,
            ["useGitWorkingTree"] = true,
        });

        var root = json.RootElement;
        Assert.True(root.TryGetProperty("error", out var error), "expected a structured error");
        Assert.Contains("enableGitAnalysis", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

        // Backward compatibility: changedFiles alone still works in the same call shape.
        using var explicitCall = await CallToolJsonAsync("ace_impact_analyze", new Dictionary<string, object?>
        {
            ["repositoryPath"] = fixture.RepositoryPath,
            ["changedFiles"] = new[] { "src/Customer.Services/CustomerService.cs" },
        });
        Assert.False(explicitCall.RootElement.TryGetProperty("error", out _), $"explicit changedFiles must still work: {explicitCall.RootElement}");
        Assert.True(explicitCall.RootElement.GetProperty("changedComponents").GetArrayLength() > 0);
    }

    // --------------------------------------------------------------- helpers

    private async Task<JsonDocument> CallToolJsonAsync(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        var text = await CallToolTextAsync(toolName, arguments);
        return JsonDocument.Parse(text);
    }

    private async Task<string> CallToolTextAsync(string toolName, IReadOnlyDictionary<string, object?> arguments)
    {
        var result = await CallWithTimeoutAsync(ct => fixture.Client.CallToolAsync(toolName, arguments, cancellationToken: ct));

        if (result.IsError == true)
        {
            var detail = string.Join(" | ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            Assert.Fail($"Tool {toolName} reported a protocol-level error: {detail}");
        }

        var block = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.False(string.IsNullOrWhiteSpace(block.Text), $"Tool {toolName} returned empty text.");
        return block.Text;
    }

    private static async Task<T> CallWithTimeoutAsync<T>(Func<CancellationToken, ValueTask<T>> operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        return await operation(timeout.Token);
    }
}
