using Ace.Core.Graph;
using Ace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ace.Core.Engines;

/// <summary>
/// Test impact engine (FR-009): affected tests are Test nodes reachable in the reverse
/// impact closure plus sources of TESTS edges pointing at changed/affected components.
/// </summary>
public sealed class TestImpactEngine
{
    private readonly ImpactEngine _impactEngine;
    private readonly ILogger<TestImpactEngine> _logger;

    public TestImpactEngine(ImpactEngine? impactEngine = null, ILogger<TestImpactEngine>? logger = null)
    {
        _impactEngine = impactEngine ?? new ImpactEngine();
        _logger = logger ?? NullLogger<TestImpactEngine>.Instance;
    }

    /// <summary>Identifies tests potentially affected by the changed files.</summary>
    public TestImpactReport Analyze(ICodeGraph graph, string repositoryPath, IEnumerable<string> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var analysis = _impactEngine.AnalyzeDetailed(graph, repositoryPath, changedFiles);
        var closure = analysis.Closure;
        var changedNodeIds = new HashSet<string>(analysis.ChangedNodeIds, StringComparer.Ordinal);

        // TESTS edges whose target is a changed or affected component.
        var testsEdges = graph.GetEdges()
            .Where(edge => edge.Type == EdgeType.Tests && closure.ContainsKey(edge.TargetId))
            .OrderBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ThenBy(edge => edge.TargetId, StringComparer.Ordinal)
            .ToList();

        // test node id -> reason (best reason wins).
        var reasons = new Dictionary<string, (int Rank, string Reason)>(StringComparer.Ordinal);

        foreach (var (nodeId, depth) in closure)
        {
            if (!graph.TryGetNode(nodeId, out var node) || node is null || node.Type != NodeType.Test)
            {
                continue;
            }

            var reason = depth == 0 ? "test file changed" : "in impact closure";
            reasons[nodeId] = (depth == 0 ? 0 : 3, reason);
        }

        foreach (var edge in testsEdges)
        {
            var target = ImpactEngine.DisplayName(graph, edge.TargetId);
            var rank = changedNodeIds.Contains(edge.TargetId) ? 1 : 2;
            var reason = changedNodeIds.Contains(edge.TargetId)
                ? $"tests changed component {target}"
                : $"tests affected component {target}";

            if (!reasons.TryGetValue(edge.SourceId, out var existing) || rank < existing.Rank)
            {
                reasons[edge.SourceId] = (rank, reason);
            }
        }

        var affectedTests = reasons
            .OrderBy(kv => ImpactEngine.DisplayName(graph, kv.Key), StringComparer.Ordinal)
            .Select(kv =>
            {
                graph.TryGetNode(kv.Key, out var node);
                return new AffectedTest
                {
                    Name = ImpactEngine.DisplayName(graph, kv.Key),
                    FilePath = node?.FilePath,
                    Reason = kv.Value.Reason,
                };
            })
            .ToList();

        // Evidence: TESTS links plus any impact-chain links that end at an affected test.
        var testNames = affectedTests.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var evidence = testsEdges
            .Select(edge => new EvidenceLink
            {
                Source = ImpactEngine.DisplayName(graph, edge.SourceId),
                Relationship = "tests",
                Target = ImpactEngine.DisplayName(graph, edge.TargetId),
            })
            .Concat(analysis.Report.Evidence.Where(link => testNames.Contains(link.Target)))
            .Distinct()
            .ToList();

        _logger.LogDebug("Test impact: {Count} affected test(s) for {Files} changed file(s)", affectedTests.Count, analysis.ChangedFiles.Count);

        return new TestImpactReport
        {
            ChangedFiles = analysis.ChangedFiles,
            AffectedTests = affectedTests,
            Evidence = evidence,
        };
    }
}
