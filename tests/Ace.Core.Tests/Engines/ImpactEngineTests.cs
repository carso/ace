using Ace.Core.Engines;
using Ace.Core.Graph;
using Ace.Core.Security;
using Ace.Core.Tests.Graph;

namespace Ace.Core.Tests.Engines;

public sealed class ImpactEngineTests
{
    private const string CustomerServiceFile = "src/Customer.Services/CustomerService.cs";

    private static ICodeGraph Graph => SampleRepoGraph.Data.Graph;

    [Fact]
    public void Analyze_ChangedCustomerService_DetectsDependentsWithDirectIndirectSplit()
    {
        var report = new ImpactEngine().Analyze(Graph, TestPaths.SampleRepo, [CustomerServiceFile]);

        // Changed components: the type plus its public members.
        Assert.Contains("CustomerService", report.ChangedComponents);
        Assert.Contains(report.ChangedComponents, component => component.StartsWith("CustomerService.", StringComparison.Ordinal));

        // Affected production components.
        Assert.Contains("OrderService", report.AffectedComponents);
        Assert.Contains("CustomerController", report.AffectedComponents);
        Assert.DoesNotContain("CustomerService", report.AffectedComponents);

        // Direct vs indirect split.
        Assert.Contains("OrderService", report.DirectAffectedComponents);
        Assert.Contains("CustomerController", report.DirectAffectedComponents);
        Assert.Contains(report.IndirectAffectedComponents, component => component.Contains("OrderServiceTests", StringComparison.Ordinal));

        // Affected projects and APIs.
        Assert.Contains("Customer.Api", report.AffectedProjects);
        Assert.Contains("Customer.Services.Tests", report.AffectedProjects);
        Assert.NotEmpty(report.AffectedApis);
        Assert.Contains(report.AffectedApis, api => api.Contains("CustomerController", StringComparison.OrdinalIgnoreCase));

        // Affected tests.
        Assert.Contains("CustomerServiceTests", report.AffectedTests);
        Assert.Contains("OrderServiceTests", report.AffectedTests);

        // Traversal bookkeeping: risk is a placeholder until the risk engine scores it.
        Assert.False(report.Truncated);
        Assert.True(report.MaxDepthReached >= 2, $"expected depth >= 2, got {report.MaxDepthReached}");
        Assert.Equal(0, report.RiskScore);
    }

    [Fact]
    public void Analyze_EvidenceTracesActualEdgeChains()
    {
        var report = new ImpactEngine().Analyze(Graph, TestPaths.SampleRepo, [CustomerServiceFile]);

        Assert.NotEmpty(report.Evidence);

        // Direct impact: OrderService references the changed service.
        Assert.Contains(report.Evidence, link =>
            link.Source == "CustomerService" &&
            link.Relationship == "referenced-by" &&
            link.Target == "OrderService");

        // Direct impact via a call chain: controller method calls the changed method.
        Assert.Contains(report.Evidence, link =>
            link.Relationship == "called-by" &&
            link.Source.StartsWith("CustomerService.", StringComparison.Ordinal) &&
            link.Target.Contains("CustomerController", StringComparison.Ordinal));

        // Indirect chain (depth 3): changed member -> OrderService.PlaceOrder ->
        // CustomerController.PlaceOrder -> OrderServiceTests method.
        Assert.Contains(report.Evidence, link =>
            link.Source == "OrderService.PlaceOrder" &&
            link.Relationship == "called-by" &&
            link.Target == "CustomerController.PlaceOrder");
        Assert.Contains(report.Evidence, link =>
            link.Source == "CustomerController.PlaceOrder" &&
            link.Relationship == "called-by" &&
            link.Target.StartsWith("OrderServiceTests.", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_AcceptsAbsoluteChangedPaths()
    {
        var absolutePath = Path.Combine(TestPaths.SampleRepo, CustomerServiceFile.Replace('/', Path.DirectorySeparatorChar));
        var report = new ImpactEngine().Analyze(Graph, TestPaths.SampleRepo, [absolutePath]);

        Assert.Contains("CustomerService", report.ChangedComponents);
        Assert.Contains("OrderService", report.AffectedComponents);
    }

    [Fact]
    public void Analyze_RejectsPathsOutsideTheRepository()
    {
        Assert.Throws<PathSecurityException>(() =>
            new ImpactEngine().Analyze(Graph, TestPaths.SampleRepo, [@"..\..\outside.cs"]));
    }

    [Fact]
    public void Analyze_UnknownFileProducesEmptyImpact()
    {
        var report = new ImpactEngine().Analyze(Graph, TestPaths.SampleRepo, ["docs/does-not-exist.md"]);

        Assert.Empty(report.ChangedComponents);
        Assert.Empty(report.AffectedComponents);
        Assert.Empty(report.Evidence);
        Assert.Equal(0, report.MaxDepthReached);
    }
}
