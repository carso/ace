using Ace.Core.Engines;
using Ace.Core.Graph;
using Ace.Core.Models;

namespace Ace.Core.Tests.Engines;

public sealed class RiskEngineTests
{
    [Theory]
    [InlineData(0, RiskLevel.Low)]
    [InlineData(33, RiskLevel.Low)]
    [InlineData(34, RiskLevel.Medium)]
    [InlineData(66, RiskLevel.Medium)]
    [InlineData(67, RiskLevel.High)]
    [InlineData(100, RiskLevel.High)]
    public void Band_MapsScoresToLevels(int score, RiskLevel expected)
        => Assert.Equal(expected, RiskRules.Band(score));

    [Fact]
    public void SmallImpact_ScoresLow()
    {
        var impact = MakeReport(affectedComponents: 1, affectedTests: 1, maxDepth: 1);
        var facts = new RiskFacts();

        var report = new RiskEngine().Analyze(impact, facts);

        Assert.Equal(RiskLevel.Low, report.RiskLevel);
        Assert.InRange(report.RiskScore, 0, RiskRules.LowUpperBoundExclusive - 1);
        Assert.NotEmpty(report.Factors);
    }

    [Fact]
    public void MediumImpact_ScoresMedium()
    {
        var impact = MakeReport(affectedComponents: 5, affectedTests: 1, maxDepth: 2, affectedProjects: ["Customer.Api", "Customer.Services"]);
        var facts = new RiskFacts { PublicApiExposed = true };

        var report = new RiskEngine().Analyze(impact, facts);

        Assert.Equal(RiskLevel.Medium, report.RiskLevel);
        Assert.InRange(report.RiskScore, RiskRules.LowUpperBoundExclusive, RiskRules.HighLowerBoundInclusive - 1);
    }

    [Fact]
    public void HugeImpact_ScoresHigh()
    {
        var impact = MakeReport(affectedComponents: 20, affectedTests: 0, maxDepth: 3, affectedProjects: ["A", "B", "C"]);
        var facts = new RiskFacts
        {
            PublicApiExposed = true,
            CrossProjectImpact = true,
            ConfigOrDatabaseChanged = true,
        };

        var report = new RiskEngine().Analyze(impact, facts);

        Assert.Equal(RiskLevel.High, report.RiskLevel);
        Assert.InRange(report.RiskScore, RiskRules.HighLowerBoundInclusive, 100);
    }

    [Fact]
    public void Factors_CarryWeightsThatNeverExceedBudget()
    {
        var impact = MakeReport(affectedComponents: 50, affectedTests: 0, maxDepth: 3);
        var facts = new RiskFacts { PublicApiExposed = true, CrossProjectImpact = true, ConfigOrDatabaseChanged = true };

        var report = new RiskEngine().Analyze(impact, facts);

        Assert.Equal(6, report.Factors.Count);
        Assert.InRange(report.Factors.Sum(factor => factor.Weight), 0, 100);
        Assert.All(report.Factors, factor => Assert.False(string.IsNullOrEmpty(factor.Name)));
    }

    [Fact]
    public void MissingTestCoverage_RaisesRiskAboveCoveredEquivalent()
    {
        var engine = new RiskEngine();
        var facts = new RiskFacts();

        var uncovered = engine.Analyze(MakeReport(affectedComponents: 6, affectedTests: 0, maxDepth: 2), facts);
        var covered = engine.Analyze(MakeReport(affectedComponents: 6, affectedTests: 6, maxDepth: 2), facts);

        Assert.True(uncovered.RiskScore > covered.RiskScore);
    }

    [Fact]
    public void RiskFacts_From_MissingNodeIds_NeverThrows()
    {
        // A concurrent graph rebuild may drop nodes mid-analysis: RiskFacts.From must
        // tolerate node ids that no longer resolve instead of throwing KeyNotFoundException.
        var graph = new InMemoryCodeGraph();
        graph.AddNode(new GraphNode { Id = "App:App.Known", Name = "Known", Type = NodeType.Class, Project = "Single.Project" });

        var analysis = new ImpactAnalysis
        {
            Report = MakeReport(affectedComponents: 1, affectedTests: 0, maxDepth: 1),
            Closure = new Dictionary<string, int> { ["App:App.Gone"] = 1 },
            ChangedNodeIds = ["App:App.Gone", "App:App.Known"],
            ChangedFiles = ["src/Known.cs"],
        };

        var facts = RiskFacts.From(graph, analysis);

        Assert.False(facts.PublicApiExposed);
        Assert.False(facts.CrossProjectImpact);
    }

    private static ImpactReport MakeReport(
        int affectedComponents,
        int affectedTests,
        int maxDepth,
        IReadOnlyList<string>? affectedProjects = null)
        => new()
        {
            ChangedComponents = ["ChangedComponent"],
            AffectedComponents = Enumerable.Range(0, affectedComponents).Select(i => $"Component{i}").ToList(),
            AffectedTests = Enumerable.Range(0, affectedTests).Select(i => $"Test{i}").ToList(),
            AffectedProjects = affectedProjects ?? ["Single.Project"],
            MaxDepthReached = maxDepth,
        };
}
