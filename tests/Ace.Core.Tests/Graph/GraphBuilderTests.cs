using System.Text.Json;
using Ace.Core.Configuration;
using Ace.Core.Discovery;
using Ace.Core.Graph;
using Ace.Core.Models;
using Ace.Core.Parsing;
using Ace.Core.Parsing.CSharp;
using Ace.Core.Platform;

namespace Ace.Core.Tests.Graph;

/// <summary>Builds the SampleRepo graph once per test run; shared by graph tests.</summary>
public static class SampleRepoGraph
{
    private static readonly Lazy<SampleRepoGraphData> LazyData = new(Build);

    public static SampleRepoGraphData Data => LazyData.Value;

    private static SampleRepoGraphData Build()
    {
        var fileSystem = new FileSystemService();
        var options = new AceOptions();
        var discovery = new RepositoryDiscovery(fileSystem, options).Discover(TestPaths.SampleRepo);
        var analyzer = new CSharpAnalyzer();

        var analyzedFiles = new List<AnalyzedFile>();
        foreach (var file in discovery.Files.Where(f => f.Category is FileCategory.Source or FileCategory.Test))
        {
            var content = File.ReadAllText(file.FullPath);
            var analysis = analyzer.AnalyzeAsync(file.RelativePath, content).GetAwaiter().GetResult();
            analyzedFiles.Add(new AnalyzedFile(file.RelativePath, analysis));
        }

        var projects = discovery.Files
            .Where(f => f.Category == FileCategory.Project)
            .Select(f => CsprojInfo.TryParse(f.RelativePath, File.ReadAllText(f.FullPath)))
            .Where(p => p is not null)
            .Cast<CsprojInfo>()
            .ToList();

        var graph = new GraphBuilder().Build(analyzedFiles, projects);
        return new SampleRepoGraphData(graph, analyzedFiles, projects);
    }
}

public sealed record SampleRepoGraphData(
    ICodeGraph Graph,
    IReadOnlyCollection<AnalyzedFile> AnalyzedFiles,
    IReadOnlyCollection<CsprojInfo> Projects);

public sealed class GraphBuilderTests
{
    private const string CustomerServiceTypeId = "Customer.Services:Customer.Services.CustomerService";
    private const string OrderServiceTypeId = "Customer.Services:Customer.Services.OrderService";
    private const string CustomerControllerTypeId = "Customer.Api:Customer.Api.CustomerController";
    private const string CustomerRepositoryInterfaceId = "Customer.Domain:Customer.Domain.ICustomerRepository";
    private const string InMemoryRepositoryTypeId = "Customer.Domain:Customer.Domain.InMemoryCustomerRepository";
    private const string CustomerServiceTestsId = "Customer.Services.Tests:Customer.Services.Tests.CustomerServiceTests";
    private const string OrderServiceTestsId = "Customer.Services.Tests:Customer.Services.Tests.OrderServiceTests";

    private static ICodeGraph Graph => SampleRepoGraph.Data.Graph;

    private GraphEdge? FindEdge(string sourceId, string targetId, EdgeType type)
        => Graph.GetEdges().FirstOrDefault(e => e.SourceId == sourceId && e.TargetId == targetId && e.Type == type);

    [Fact]
    public void Build_CreatesProjectNamespaceAndTypeNodes()
    {
        foreach (var projectName in new[] { "Customer.Domain", "Customer.Services", "Customer.Api", "Customer.Services.Tests" })
        {
            Assert.True(Graph.TryGetNode(projectName, out var projectNode), $"missing project node {projectName}");
            Assert.Equal(NodeType.Project, projectNode!.Type);
        }

        Assert.True(Graph.TryGetNode("Customer.Services:Customer.Services", out var namespaceNode));
        Assert.Equal(NodeType.Namespace, namespaceNode!.Type);

        Assert.True(Graph.TryGetNode(CustomerServiceTypeId, out var serviceNode));
        Assert.Equal(NodeType.Class, serviceNode!.Type);
        Assert.Equal("Customer.Services", serviceNode.Project);
        Assert.Equal("Customer.Services", serviceNode.Namespace);
        Assert.Equal("src/Customer.Services/CustomerService.cs", serviceNode.FilePath);

        Assert.True(Graph.TryGetNode(CustomerRepositoryInterfaceId, out var interfaceNode));
        Assert.Equal(NodeType.Interface, interfaceNode!.Type);

        // Test classes become Test nodes.
        Assert.True(Graph.TryGetNode(CustomerServiceTestsId, out var testNode));
        Assert.Equal(NodeType.Test, testNode!.Type);
    }

    [Fact]
    public void Build_CreatesMethodNodesWithContainsEdges()
    {
        var methodId = $"{CustomerServiceTypeId}#CalculateDiscount";
        Assert.True(Graph.TryGetNode(methodId, out var methodNode));
        Assert.Equal(NodeType.Method, methodNode!.Type);
        Assert.Contains("CalculateDiscount", methodNode.Metadata["signature"]?.ToString());

        var contains = FindEdge(CustomerServiceTypeId, methodId, EdgeType.Contains);
        Assert.NotNull(contains);
        Assert.Equal(Confidence.Observed, contains!.Confidence);
        Assert.Equal(1.0, contains.ConfidenceScore);

        var namespaceContains = FindEdge("Customer.Services:Customer.Services", CustomerServiceTypeId, EdgeType.Contains);
        Assert.NotNull(namespaceContains);
    }

    [Fact]
    public void Build_ControllerCallsServiceChain_WithInferredConfidenceAndEvidence()
    {
        var controllerMethodId = $"{CustomerControllerTypeId}#GetCustomer";
        var serviceMethodId = $"{CustomerServiceTypeId}#GetCustomer";

        var call = FindEdge(controllerMethodId, serviceMethodId, EdgeType.Calls);
        Assert.NotNull(call);
        Assert.Equal(Confidence.Inferred, call!.Confidence);
        Assert.Equal(GraphBuilder.CallConfidenceScore, call.ConfidenceScore);
        Assert.Equal("invocation", call.Evidence);
        Assert.StartsWith("src/Customer.Api/CustomerController.cs:", call.Location);

        var orderToDiscount = FindEdge($"{OrderServiceTypeId}#PlaceOrder", $"{CustomerServiceTypeId}#CalculateDiscount", EdgeType.Calls);
        Assert.NotNull(orderToDiscount);
        Assert.Equal(Confidence.Inferred, orderToDiscount!.Confidence);

        // Controller type references the services it depends on (constructor parameters/fields).
        Assert.NotNull(FindEdge(CustomerControllerTypeId, CustomerServiceTypeId, EdgeType.References));
        Assert.NotNull(FindEdge(CustomerControllerTypeId, OrderServiceTypeId, EdgeType.References));
        Assert.NotNull(FindEdge(OrderServiceTypeId, CustomerServiceTypeId, EdgeType.References));
    }

    [Fact]
    public void Build_ResolvesImplementsAndInheritsFromBaseLists()
    {
        var implements = FindEdge(InMemoryRepositoryTypeId, CustomerRepositoryInterfaceId, EdgeType.Implements);
        Assert.NotNull(implements);
        Assert.Equal(Confidence.Calculated, implements!.Confidence);
        Assert.Equal("base-list", implements.Evidence);
    }

    [Fact]
    public void Build_TestClassesLinkToProductionTypes()
    {
        var tests = FindEdge(CustomerServiceTestsId, CustomerServiceTypeId, EdgeType.Tests);
        Assert.NotNull(tests);
        Assert.Equal(Confidence.Inferred, tests!.Confidence);
        Assert.Equal(GraphBuilder.TestNamingConfidenceScore, tests.ConfidenceScore);
        Assert.Equal("naming-convention", tests.Evidence);

        Assert.NotNull(FindEdge(OrderServiceTestsId, OrderServiceTypeId, EdgeType.Tests));
    }

    [Fact]
    public void Build_ProjectDependenciesFromCsproj()
    {
        Assert.NotNull(FindEdge("Customer.Services", "Customer.Domain", EdgeType.DependsOn));
        Assert.NotNull(FindEdge("Customer.Api", "Customer.Services", EdgeType.DependsOn));
        Assert.NotNull(FindEdge("Customer.Services.Tests", "Customer.Services", EdgeType.DependsOn));
        Assert.NotNull(FindEdge("Customer.Services.Tests", "Customer.Domain", EdgeType.DependsOn));

        var projectDependency = FindEdge("Customer.Api", "Customer.Services", EdgeType.DependsOn);
        Assert.Equal(Confidence.Observed, projectDependency!.Confidence);
        Assert.Equal("project-reference", projectDependency.Evidence);

        // Package references produce Package nodes and project→package DEPENDS_ON edges.
        Assert.True(Graph.TryGetNode("package:xunit", out var packageNode));
        Assert.Equal(NodeType.Package, packageNode!.Type);
        var packageDependency = FindEdge("Customer.Services.Tests", "package:xunit", EdgeType.DependsOn);
        Assert.NotNull(packageDependency);
        Assert.Equal("package-reference", packageDependency!.Evidence);
    }

    [Fact]
    public void Build_EveryInferredEdgeCarriesEvidenceAndSubUnitConfidence()
    {
        var inferred = Graph.GetEdges().Where(e => e.Confidence == Confidence.Inferred).ToList();
        Assert.NotEmpty(inferred);
        Assert.All(inferred, e =>
        {
            Assert.True(e.ConfidenceScore < 1.0);
            Assert.False(string.IsNullOrEmpty(e.Evidence));
            Assert.False(string.IsNullOrEmpty(e.Location));
        });
    }

    [Fact]
    public void Build_ProducesNonTrivialGraph()
    {
        // Regression anchor: SampleRepo must yield a meaningfully populated graph.
        Assert.True(Graph.GetNodes().Count >= 30, $"expected >= 30 nodes, got {Graph.GetNodes().Count}");
        Assert.True(Graph.GetEdges().Count >= 60, $"expected >= 60 edges, got {Graph.GetEdges().Count}");
    }

    [Fact]
    public void FindNodesByName_LocatesSymbols()
    {
        var matches = Graph.FindNodesByName("CustomerService");
        Assert.Contains(matches, n => n.Id == CustomerServiceTypeId);
        Assert.Empty(Graph.FindNodesByName("no-such-symbol-anywhere"));
    }

    [Fact]
    public void JsonGraphStore_RoundTripsGraphThroughDisk()
    {
        var temp = TestPaths.CreateTempCopyOfSampleRepo();
        try
        {
            var store = new JsonGraphStore(new FileSystemService());
            store.Save(Graph, temp);

            Assert.True(File.Exists(JsonGraphStore.GetGraphPath(temp)));

            var loaded = store.Load(temp);
            Assert.NotNull(loaded);
            Assert.Equal(Graph.GetNodes().Count, loaded!.GetNodes().Count);
            Assert.Equal(Graph.GetEdges().Count, loaded.GetEdges().Count);

            Assert.True(loaded.TryGetNode(CustomerServiceTypeId, out var node));
            Assert.Equal(NodeType.Class, node!.Type);

            var reloadedCall = loaded.GetEdges().First(e =>
                e.SourceId == $"{CustomerControllerTypeId}#GetCustomer" &&
                e.TargetId == $"{CustomerServiceTypeId}#GetCustomer" &&
                e.Type == EdgeType.Calls);
            Assert.Equal(Confidence.Inferred, reloadedCall.Confidence);
        }
        finally
        {
            TestPaths.DeleteDirectoryQuietly(temp);
        }
    }

    [Fact]
    public void JsonGraphStore_LoadMissingOrCorrupt_ReturnsNull()
    {
        var temp = TestPaths.CreateTempCopyOfSampleRepo();
        try
        {
            var store = new JsonGraphStore(new FileSystemService());
            Assert.Null(store.Load(temp));

            Directory.CreateDirectory(Path.Combine(temp, ".ace"));
            File.WriteAllText(JsonGraphStore.GetGraphPath(temp), "not json at all");
            Assert.Null(store.Load(temp));
        }
        finally
        {
            TestPaths.DeleteDirectoryQuietly(temp);
        }
    }

    [Fact]
    public void JsonGraphStore_StampsRepository_AndRejectsForeignGraphs()
    {
        var temp = TestPaths.CreateTempCopyOfSampleRepo();
        var otherRoot = TestPaths.CreateTempCopyOfSampleRepo();
        try
        {
            var store = new JsonGraphStore(new FileSystemService());
            store.Save(Graph, temp);

            // The persisted graph carries the repository root it was built for.
            using var document = JsonDocument.Parse(File.ReadAllText(JsonGraphStore.GetGraphPath(temp)));
            Assert.Equal(temp, document.RootElement.GetProperty("repository").GetString());

            Assert.NotNull(store.Load(temp));

            // A graph stamped for a different repository root is treated as absent.
            Assert.Null(store.Load(otherRoot));
        }
        finally
        {
            TestPaths.DeleteDirectoryQuietly(temp);
            TestPaths.DeleteDirectoryQuietly(otherRoot);
        }
    }
}
