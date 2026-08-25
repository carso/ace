using Ace.Core.Engines;
using Ace.Core.Graph;
using Ace.Core.Models;
using Ace.Core.Tests.Graph;

namespace Ace.Core.Tests.Engines;

public sealed class RegressionEngineTests
{
    private const string CustomerServiceFile = "src/Customer.Services/CustomerService.cs";

    private static ICodeGraph Graph => SampleRepoGraph.Data.Graph;

    [Fact]
    public void Analyze_ComposesImpactRiskAndTests()
    {
        var scope = new RegressionEngine().Analyze(Graph, TestPaths.SampleRepo, [CustomerServiceFile]);

        // Potential impact: production components reachable from the change.
        Assert.NotEmpty(scope.PotentialImpact);
        Assert.Contains("OrderService", scope.PotentialImpact);
        Assert.Contains("CustomerController", scope.PotentialImpact);
        Assert.DoesNotContain(scope.PotentialImpact, component => component.Contains("Tests", StringComparison.Ordinal));

        // Recommended tests: both test classes from the test impact engine.
        Assert.Contains(scope.AffectedTests, test => test.Name == "CustomerServiceTests");
        Assert.Contains(scope.AffectedTests, test => test.Name == "OrderServiceTests");

        Assert.Contains(CustomerServiceFile, scope.ChangedFiles);
        Assert.False(string.IsNullOrWhiteSpace(scope.RecommendedScope));
    }

    [Fact]
    public void Analyze_ProducesHumanReadableReason()
    {
        var scope = new RegressionEngine().Analyze(Graph, TestPaths.SampleRepo, [CustomerServiceFile]);

        Assert.NotEmpty(scope.Notes);
        Assert.Contains(scope.Notes, note =>
            note.Contains("production component", StringComparison.Ordinal) &&
            note.Contains("test component", StringComparison.Ordinal));
        Assert.Contains(scope.Notes, note => note.Contains("Risk ", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_WideBlastRadiusEscalatesScope()
    {
        // Changing a shared domain type ripples through services, controller and tests.
        var scope = new RegressionEngine().Analyze(Graph, TestPaths.SampleRepo, ["src/Customer.Domain/Customer.cs"]);

        Assert.True(scope.AffectedTests.Count >= 2, $"expected >= 2 affected tests, got {scope.AffectedTests.Count}");
        Assert.Contains("CustomerService", scope.PotentialImpact);
        Assert.True(scope.RiskLevel is RiskLevel.Medium or RiskLevel.High,
            $"expected elevated risk for a shared domain change, got {scope.RiskLevel}");
        Assert.NotEqual("Run affected unit tests", scope.RecommendedScope);
    }

    [Fact]
    public void Analyze_NoImpactSuggestsManualReview()
    {
        var scope = new RegressionEngine().Analyze(Graph, TestPaths.SampleRepo, ["src/Customer.Api/appsettings.json"]);

        Assert.Empty(scope.PotentialImpact);
        Assert.Empty(scope.AffectedTests);
        Assert.Equal(RiskLevel.Low, scope.RiskLevel);
        Assert.Equal("No automated tests mapped; review change manually", scope.RecommendedScope);
    }
}
