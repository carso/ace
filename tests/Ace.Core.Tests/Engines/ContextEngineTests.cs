using Ace.Core.Configuration;
using Ace.Core.Engines;
using Ace.Core.Graph;
using Ace.Core.Indexing;
using Ace.Core.Models;
using Ace.Core.Tests.Graph;

namespace Ace.Core.Tests.Engines;

public sealed class ContextEngineTests
{
    private static ICodeGraph Graph => SampleRepoGraph.Data.Graph;

    [Fact]
    public void GetContext_RanksTiersInPriorityOrder()
    {
        var items = new ContextEngine().GetContext(Graph, "CustomerService");

        Assert.NotEmpty(items);

        // Tier 1 (direct code) leads.
        Assert.Equal(1, items[0].Tier);
        Assert.Contains("CustomerService", items[0].Title, StringComparison.Ordinal);

        // Tiers never decrease: direct code -> dependencies -> impacted -> ... -> repo context.
        Assert.True(items.Zip(items.Skip(1)).All(pair => pair.First.Tier <= pair.Second.Tier));

        Assert.Contains(items, item => item.Tier == 2); // dependencies
        Assert.Contains(items, item => item.Tier == 3 && item.Title == "OrderService"); // impacted
        Assert.Contains(items, item => item.Tier == 4 && item.Title == "CustomerServiceTests"); // tests
        Assert.Contains(items, item => item.Tier == 7); // repository summary

        // Direct code ranks strictly above the repository summary tier.
        Assert.True(items.First(item => item.Tier == 1).Score > items.First(item => item.Tier == 7).Score);
    }

    [Fact]
    public void GetContext_ArchitectureTierRanksAfterDirectCode()
    {
        var graph = new InMemoryCodeGraph();
        graph.AddNode(new GraphNode { Id = "App:App.CustomerRepository", Name = "CustomerRepository", Type = NodeType.Class, Project = "App", FilePath = "src/CustomerRepository.cs" });
        graph.AddNode(new GraphNode { Id = "App:App.CustomerController", Name = "CustomerController", Type = NodeType.Class, Project = "App", FilePath = "src/CustomerController.cs" });
        graph.AddEdge(new GraphEdge { SourceId = "App:App.CustomerRepository", TargetId = "App:App.CustomerController", Type = EdgeType.Calls, Location = "src/CustomerRepository.cs:30" });

        var options = new AceOptions { EnableArchitectureAnalysis = true };
        var violations = new ArchitectureEngine().Analyze(graph, options);
        Assert.NotEmpty(violations);

        var items = new ContextEngine().GetContext(graph, "CustomerRepository", violations: violations).ToList();

        var directIndex = items.FindIndex(item => item.Tier == 1);
        var architectureIndex = items.FindIndex(item => item.Tier == 6);
        Assert.True(directIndex >= 0, "expected a tier-1 direct code item");
        Assert.True(architectureIndex >= 0, "expected a tier-6 architecture item");
        Assert.True(directIndex < architectureIndex, "direct code must rank before architecture info");
    }

    [Fact]
    public void GetContext_IncludesConfigurationTierFromIndex()
    {
        var index = new RepositoryIndex { Repository = TestPaths.SampleRepo };
        index.Files["src/Customer.Services/settings.json"] = new StoredIndexEntry { Category = "config" };
        index.Files["global.json"] = new StoredIndexEntry { Category = "config" };

        var items = new ContextEngine().GetContext(Graph, "CustomerService", index: index);

        var configItems = items.Where(item => item.Tier == 5).ToList();
        Assert.Equal(2, configItems.Count);
        // The config file next to the queried code scores above unrelated config.
        Assert.True(configItems.First(item => item.Path == "src/Customer.Services/settings.json").Score >
                    configItems.First(item => item.Path == "global.json").Score);
    }

    [Fact]
    public void GetContext_FilePathQueryResolvesDeclarations()
    {
        var items = new ContextEngine().GetContext(Graph, "src/Customer.Services/CustomerService.cs");

        Assert.Equal(1, items[0].Tier);
        Assert.Contains(items, item => item.Tier == 1 && item.Title == "CustomerService");
    }

    [Fact]
    public void GetContext_EnforcesBudgetWithTruncationMarker()
    {
        var all = new ContextEngine().GetContext(Graph, "CustomerService", maxItems: 100);
        Assert.True(all.Count > 3, "fixture should produce more than 3 context items");

        var budgeted = new ContextEngine().GetContext(Graph, "CustomerService", maxItems: 3);

        Assert.Equal(3, budgeted.Count);
        Assert.Equal(ContextEngine.TruncationTier, budgeted[^1].Tier);
        Assert.Contains("truncated", budgeted[^1].Title, StringComparison.OrdinalIgnoreCase);

        // The marker reports exactly the number of dropped content items: total items
        // minus the content items kept (the marker itself occupies one budget slot).
        Assert.Equal(
            $"[truncated] {all.Count - (budgeted.Count - 1)} more context item(s) available; refine the query or raise maxItems",
            budgeted[^1].Title);
    }

    [Fact]
    public void GetContext_UnknownQueryStillReturnsRepositorySummary()
    {
        var items = new ContextEngine().GetContext(Graph, "zzz-no-such-symbol-zzz");

        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Equal(7, item.Tier));
    }
}

public sealed class CodeSearchServiceTests
{
    private static ICodeGraph Graph => SampleRepoGraph.Data.Graph;

    [Fact]
    public void Search_FindsCustomerServiceCaseInsensitively()
    {
        var results = new CodeSearchService().Search(Graph, "customerservice");

        Assert.Contains(results, (Predicate<SymbolLocation>)(location => location.Id == "Customer.Services:Customer.Services.CustomerService"));
        var type = results.First(location => location.Id == "Customer.Services:Customer.Services.CustomerService");
        Assert.Equal("CustomerService", type.Name);
        Assert.Equal("Class", type.Kind);
        Assert.Equal("Customer.Services", type.Project);
        Assert.Equal("src/Customer.Services/CustomerService.cs", type.FilePath);
        Assert.NotNull(type.Line);
        Assert.True(type.Line > 0);

        // The test class also matches the substring.
        Assert.Contains(results, location => location.Name == "CustomerServiceTests");
    }

    [Fact]
    public void Search_MethodMatchesCarryMemberKind()
    {
        var results = new CodeSearchService().Search(Graph, "CalculateDiscount");

        Assert.Contains(results, (Predicate<SymbolLocation>)(location => location.Id.EndsWith("#CalculateDiscount", StringComparison.Ordinal)));
        var method = results.First(location => location.Id.EndsWith("#CalculateDiscount", StringComparison.Ordinal));
        Assert.Equal("Method", method.Kind);
    }

    [Fact]
    public void Search_EmptyOrUnknownQueryReturnsNothing()
    {
        Assert.Empty(new CodeSearchService().Search(Graph, "   "));
        Assert.Empty(new CodeSearchService().Search(Graph, "zzz-no-such-symbol-zzz"));
    }
}
