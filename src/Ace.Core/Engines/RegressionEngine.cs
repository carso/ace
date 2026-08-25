using Ace.Core.Graph;
using Ace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ace.Core.Engines;

/// <summary>
/// Regression intelligence (FR-010): composes impact, risk and test impact into a
/// recommended regression scope with human-readable reasoning.
/// </summary>
public sealed class RegressionEngine
{
    private static readonly HashSet<NodeType> TestLikeTypes = [NodeType.Test];

    private readonly ImpactEngine _impactEngine;
    private readonly RiskEngine _riskEngine;
    private readonly TestImpactEngine _testImpactEngine;
    private readonly ILogger<RegressionEngine> _logger;

    public RegressionEngine(
        ImpactEngine? impactEngine = null,
        RiskEngine? riskEngine = null,
        TestImpactEngine? testImpactEngine = null,
        ILogger<RegressionEngine>? logger = null)
    {
        _impactEngine = impactEngine ?? new ImpactEngine();
        _riskEngine = riskEngine ?? new RiskEngine();
        _testImpactEngine = testImpactEngine ?? new TestImpactEngine(_impactEngine);
        _logger = logger ?? NullLogger<RegressionEngine>.Instance;
    }

    /// <summary>Produces the recommended regression scope for a change set.</summary>
    public RegressionScope Analyze(ICodeGraph graph, string repositoryPath, IEnumerable<string> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var analysis = _impactEngine.AnalyzeDetailed(graph, repositoryPath, changedFiles);
        var impact = analysis.Report;
        var facts = RiskFacts.From(graph, analysis);
        var risk = _riskEngine.Analyze(impact, facts);
        var testReport = _testImpactEngine.Analyze(graph, repositoryPath, analysis.ChangedFiles);

        // Production components = affected closure nodes that are neither tests (or members
        // of test types) nor structural nodes.
        var productionComponents = analysis.Closure
            .Where(kv => kv.Value > 0)
            .Select(kv => kv.Key)
            .Where(id => !IsMemberOfTestType(graph, id))
            .Select(graph.GetNode)
            .Where(node => !TestLikeTypes.Contains(node.Type) &&
                           node.Type is not NodeType.Project and not NodeType.Namespace and not NodeType.Package)
            .Select(node => ImpactEngine.DisplayName(graph, node.Id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var productionCount = productionComponents.Count;
        var testCount = testReport.AffectedTests.Count;

        var recommendedScope = risk.RiskLevel switch
        {
            RiskLevel.Low when testCount > 0 => "Run affected unit tests",
            RiskLevel.Low => "No automated tests mapped; review change manually",
            RiskLevel.Medium => "Run affected unit and integration tests",
            _ => "Full regression recommended",
        };

        var notes = new List<string>
        {
            $"Change touches {impact.ChangedComponents.Count} component(s) in {analysis.ChangedFiles.Count} file(s); " +
            $"referenced by {productionCount} production component(s) and {testCount} test component(s) " +
            $"across {impact.AffectedProjects.Count} project(s).",
            $"Risk {risk.RiskLevel} (score {risk.RiskScore}/100): " +
            string.Join("; ", risk.Factors
                .Where(factor => factor.Weight > 0)
                .OrderByDescending(factor => factor.Weight)
                .Select(factor => $"{factor.Name} +{factor.Weight:0.#}")),
        };

        if (impact.Truncated)
        {
            notes.Add("Impact traversal was truncated at the visit/depth cap; results may be incomplete.");
        }

        if (impact.AffectedApis.Count > 0)
        {
            notes.Add($"Public API surface affected: {string.Join(", ", impact.AffectedApis.Take(5))}.");
        }

        _logger.LogDebug(
            "Regression scope: {Level}, {Tests} test(s), {Components} production component(s)",
            risk.RiskLevel, testCount, productionCount);

        return new RegressionScope
        {
            RiskLevel = risk.RiskLevel,
            RecommendedScope = recommendedScope,
            ChangedFiles = analysis.ChangedFiles,
            PotentialImpact = productionComponents,
            AffectedTests = testReport.AffectedTests,
            Notes = notes,
        };
    }

    /// <summary>True for member nodes ("Type#Member") declared on a Test-type node.</summary>
    private static bool IsMemberOfTestType(ICodeGraph graph, string nodeId)
    {
        var hashIndex = nodeId.IndexOf('#');
        if (hashIndex < 0)
        {
            return false;
        }

        return graph.TryGetNode(nodeId[..hashIndex], out var declaringType) &&
               declaringType?.Type == NodeType.Test;
    }
}
