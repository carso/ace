using Ace.Core.Engines;
using Ace.Core.Graph;
using Ace.Core.Tests.Graph;

namespace Ace.Core.Tests.Engines;

public sealed class TestImpactEngineTests
{
    private const string CustomerServiceFile = "src/Customer.Services/CustomerService.cs";

    private static ICodeGraph Graph => SampleRepoGraph.Data.Graph;

    [Fact]
    public void Analyze_ChangedCustomerService_FindsBothTestClasses()
    {
        var report = new TestImpactEngine().Analyze(Graph, TestPaths.SampleRepo, [CustomerServiceFile]);

        var names = report.AffectedTests.Select(test => test.Name).ToList();
        Assert.Contains("CustomerServiceTests", names);
        Assert.Contains("OrderServiceTests", names);

        Assert.All(report.AffectedTests, test =>
        {
            Assert.False(string.IsNullOrEmpty(test.FilePath));
            Assert.False(string.IsNullOrEmpty(test.Reason));
            Assert.StartsWith("tests/", test.FilePath, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Analyze_ExplainsWhyEachTestIsAffected()
    {
        var report = new TestImpactEngine().Analyze(Graph, TestPaths.SampleRepo, [CustomerServiceFile]);

        // Direct: the TESTS edge targets the changed component itself.
        Assert.Contains(report.AffectedTests, test =>
            test.Name == "CustomerServiceTests" &&
            test.Reason!.StartsWith("tests changed component", StringComparison.Ordinal));

        // Indirect: the TESTS edge targets an affected component (OrderService).
        Assert.Contains(report.AffectedTests, test =>
            test.Name == "OrderServiceTests" &&
            test.Reason!.StartsWith("tests affected component", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_CarriesTestsEdgeEvidence()
    {
        var report = new TestImpactEngine().Analyze(Graph, TestPaths.SampleRepo, [CustomerServiceFile]);

        Assert.NotEmpty(report.Evidence);
        Assert.Contains(report.Evidence, link =>
            link.Relationship == "tests" &&
            link.Source == "CustomerServiceTests" &&
            link.Target == "CustomerService");
        Assert.Contains(CustomerServiceFile, report.ChangedFiles);
    }

    [Fact]
    public void Analyze_NonCodeChangeYieldsNoTests()
    {
        // Configuration files declare no graph nodes, so nothing becomes a traversal seed.
        var report = new TestImpactEngine().Analyze(Graph, TestPaths.SampleRepo, ["src/Customer.Api/appsettings.json"]);

        Assert.Empty(report.AffectedTests);
    }
}
