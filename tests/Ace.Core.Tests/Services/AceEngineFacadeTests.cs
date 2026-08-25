using System.Text.Json;
using Ace.Core.Indexing;
using Ace.Core.Models;
using Ace.Core.Services;

namespace Ace.Core.Tests.Services;

public sealed class AceEngineFacadeTests : IDisposable
{
    private readonly string _repo = TestPaths.CreateTempCopyOfSampleRepo();

    public void Dispose() => TestPaths.DeleteDirectoryQuietly(_repo);

    [Fact]
    public async Task FirstCallIndexesAndPersistsArtifacts()
    {
        var facade = new AceEngineFacade();

        var status = await facade.GetStatusAsync(_repo);

        Assert.True(status.Indexed);
        Assert.True(status.FileCount > 0);
        Assert.True(status.NodeCount >= 30, $"expected >= 30 nodes, got {status.NodeCount}");
        Assert.True(status.EdgeCount >= 60, $"expected >= 60 edges, got {status.EdgeCount}");
        Assert.False(status.Stale);
        Assert.Empty(status.FailedFiles);
        Assert.Equal(RepositoryIndex.CurrentAnalyzerVersion, status.AnalyzerVersion);
        Assert.Contains("C#", status.Languages);
        Assert.Contains("Customer.Services.Tests", status.TestProjects);

        Assert.True(File.Exists(Path.Combine(_repo, ".ace", "index.json")));
        Assert.True(File.Exists(Path.Combine(_repo, ".ace", "graph.json")));
    }

    [Fact]
    public async Task SecondCallReusesCachedSessionWithoutRebuild()
    {
        var facade = new AceEngineFacade();
        var first = await facade.GetStatusAsync(_repo);

        var graphPath = Path.Combine(_repo, ".ace", "graph.json");
        var writtenAt = File.GetLastWriteTimeUtc(graphPath);

        var second = await facade.GetStatusAsync(_repo);

        Assert.Equal(first.NodeCount, second.NodeCount);
        Assert.Equal(first.EdgeCount, second.EdgeCount);
        // The cached session must not touch the persisted graph again.
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(graphPath));
    }

    [Fact]
    public async Task ImpactAnalysisEndToEndWithRiskMerged()
    {
        var facade = new AceEngineFacade();

        var report = await facade.AnalyzeImpactAsync(_repo, ["src/Customer.Services/CustomerService.cs"]);

        Assert.Contains("CustomerService", report.ChangedComponents);
        Assert.Contains("OrderService", report.AffectedComponents);
        Assert.Contains("CustomerController", report.AffectedComponents);
        Assert.NotEmpty(report.AffectedTests);
        Assert.NotEmpty(report.Evidence);
        Assert.True(report.RiskScore > 0, "risk engine must fill the placeholder score");
        Assert.NotEqual(Ace.Core.Models.RiskLevel.Low, report.RiskLevel);
    }

    [Fact]
    public async Task FacadeExposesAllEnginesOnSampleRepo()
    {
        var facade = new AceEngineFacade();

        // Repository analyze.
        var context = await facade.AnalyzeRepositoryAsync(_repo);
        Assert.True(context.ProjectCount >= 4);

        // Code search (case-insensitive).
        var search = await facade.SearchCodeAsync(_repo, "customerservice");
        Assert.Contains(search, location => location.Name == "CustomerService");

        // Context retrieval.
        var contextItems = await facade.GetContextAsync(_repo, "CustomerService");
        Assert.NotEmpty(contextItems);
        Assert.Equal(1, contextItems[0].Tier);

        // Dependencies.
        var dependencies = await facade.GetDependenciesAsync(_repo, "CustomerController");
        Assert.Contains(dependencies, node => node.Name == "CustomerService");

        // Graph query.
        var neighbors = await facade.QueryGraphAsync(_repo, "Customer.Services:Customer.Services.CustomerService");
        Assert.NotEmpty(neighbors);

        // Risk, tests, regression.
        var risk = await facade.AnalyzeRiskAsync(_repo, ["src/Customer.Services/CustomerService.cs"]);
        Assert.True(risk.RiskScore > 0);
        Assert.NotEmpty(risk.Factors);

        var tests = await facade.GetAffectedTestsAsync(_repo, ["src/Customer.Services/CustomerService.cs"]);
        Assert.Contains(tests.AffectedTests, test => test.Name == "CustomerServiceTests");

        var scope = await facade.GetRegressionScopeAsync(_repo, ["src/Customer.Services/CustomerService.cs"]);
        Assert.NotEmpty(scope.PotentialImpact);
        Assert.NotEmpty(scope.Notes);

        // Architecture (enabled by default; SampleRepo has no default-rule violations).
        var violations = await facade.AnalyzeArchitectureAsync(_repo);
        Assert.Empty(violations);
    }

    [Fact]
    public async Task BuildGraphAsyncForcesRebuildAndPersists()
    {
        var facade = new AceEngineFacade();
        await facade.GetStatusAsync(_repo);
        var graphPath = Path.Combine(_repo, ".ace", "graph.json");
        var writtenAt = File.GetLastWriteTimeUtc(graphPath);
        File.SetLastWriteTimeUtc(graphPath, writtenAt.AddSeconds(-5)); // make any rewrite observable

        var info = await facade.BuildGraphAsync(_repo);

        Assert.True(info.NodeCount >= 30);
        Assert.True(info.EdgeCount >= 60);
        Assert.True(File.GetLastWriteTimeUtc(graphPath) > writtenAt.AddSeconds(-5));
        Assert.Equal(graphPath, info.PersistedPath);
    }

    [Fact]
    public async Task MissingRepositoryThrowsStructuredError()
    {
        var facade = new AceEngineFacade();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            facade.GetStatusAsync(Path.Combine(_repo, "does-not-exist")));
    }

    [Fact]
    public async Task EscapingIndexPath_ArtifactsStayInsideRepoDotAce()
    {
        // A hostile ace.json must not move ACE artifacts out of the repository (SR-005).
        File.WriteAllText(
            Path.Combine(_repo, "ace.json"),
            """{ "ace": { "indexPath": "..\\_evil" } }""");

        var facade = new AceEngineFacade();
        var status = await facade.GetStatusAsync(_repo);

        Assert.True(status.Indexed);
        Assert.True(File.Exists(Path.Combine(_repo, ".ace", "index.json")));
        Assert.True(File.Exists(Path.Combine(_repo, ".ace", "graph.json")));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(_repo)!, "_evil")));
    }

    [Fact]
    public async Task AbsoluteIndexPath_ArtifactsStayInsideRepoDotAce()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ace-evil-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(
            Path.Combine(_repo, "ace.json"),
            JsonSerializer.Serialize(new { ace = new { indexPath = outside } }));

        try
        {
            var facade = new AceEngineFacade();
            var status = await facade.GetStatusAsync(_repo);

            Assert.True(status.Indexed);
            Assert.True(File.Exists(Path.Combine(_repo, ".ace", "index.json")));
            Assert.False(Directory.Exists(outside));
        }
        finally
        {
            TestPaths.DeleteDirectoryQuietly(outside);
        }
    }

    [Fact]
    public async Task RefreshPicksUpNewlyAddedSourceFile()
    {
        var facade = new AceEngineFacade();
        var before = await facade.GetStatusAsync(_repo); // initial index, cached

        Assert.Empty(await facade.SearchCodeAsync(_repo, "RefreshProbeService"));

        // A brand-new source file appears on disk after the session was cached.
        File.WriteAllText(
            Path.Combine(_repo, "src", "Customer.Services", "RefreshProbe.cs"),
            "namespace Customer.Services; public sealed class RefreshProbeService { }");

        var context = await facade.AnalyzeRepositoryAsync(_repo); // refresh path

        Assert.Equal(before.FileCount + 1, context.FileCount);
        var search = await facade.SearchCodeAsync(_repo, "RefreshProbeService");
        Assert.Contains(search, location => location.Name == "RefreshProbeService");

        // ace_graph_build must refresh too.
        File.WriteAllText(
            Path.Combine(_repo, "src", "Customer.Services", "RefreshProbeTwo.cs"),
            "namespace Customer.Services; public sealed class RefreshProbeTwoService { }");
        await facade.BuildGraphAsync(_repo);
        var searchTwo = await facade.SearchCodeAsync(_repo, "RefreshProbeTwoService");
        Assert.Contains(searchTwo, location => location.Name == "RefreshProbeTwoService");
    }

    [Fact]
    public async Task QueryGraph_UnknownNodeId_FallsBackToNameResolution()
    {
        var facade = new AceEngineFacade();

        // "CustomerService" is a symbol name, not an exact node id — MCP must behave
        // like the CLI and resolve it by name instead of returning an empty list.
        var neighbors = await facade.QueryGraphAsync(_repo, "CustomerService");

        Assert.NotEmpty(neighbors);

        var unknown = await facade.QueryGraphAsync(_repo, "zzz-no-such-node-zzz");
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task StatusJson_ExposesApiVersionContract()
    {
        var facade = new AceEngineFacade();
        var status = await facade.GetStatusAsync(_repo);

        // The CLI prints exactly this record as JSON (SRS §21 contract version).
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(status, AceJson.Options));
        Assert.Equal("ACE v1", document.RootElement.GetProperty("apiVersion").GetString());
    }

    [Fact]
    public async Task ResolveChangedFiles_ExplicitFilesWorkWithoutGit()
    {
        var facade = new AceEngineFacade();

        // changedFiles stays the primary input and works with git disabled (default).
        var resolved = await facade.ResolveChangedFilesAsync(_repo, ["src/Customer.Services/CustomerService.cs"]);

        var file = Assert.Single(resolved);
        Assert.Equal("src/Customer.Services/CustomerService.cs", file);
    }

    [Fact]
    public async Task ResolveChangedFiles_GitWorkingTreeRefusedWhenGitAnalysisDisabled()
    {
        var facade = new AceEngineFacade(); // SampleRepo copy has no ace.json -> enableGitAnalysis=false

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            facade.ResolveChangedFilesAsync(_repo, [], useGitWorkingTree: true));

        Assert.Contains("enableGitAnalysis", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveChangedFiles_GitDiffRangeRefusedWhenGitAnalysisDisabled()
    {
        var facade = new AceEngineFacade();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            facade.ResolveChangedFilesAsync(_repo, [], gitDiffRange: "HEAD~1..HEAD"));

        Assert.Contains("enableGitAnalysis", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveChangedFiles_NoInputsAtAll_IsRejected()
    {
        var facade = new AceEngineFacade();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            facade.ResolveChangedFilesAsync(_repo, []));
    }
}
