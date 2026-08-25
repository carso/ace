using Ace.Core.Configuration;
using Ace.Core.Graph;
using Ace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ace.Core.Engines;

/// <summary>
/// Architecture analysis (FR-011): classifies graph nodes into layers by naming
/// conventions and scans CALLS/REFERENCES/DEPENDS_ON edges against configurable
/// allowed-direction rules. Inner layers must not depend on outer layers:
/// Controller→Service→Repository is valid, Repository→Controller is a violation,
/// Domain→Infrastructure a potential violation.
/// </summary>
public sealed class ArchitectureEngine
{
    /// <summary>Severity for clear layer inversions (gap of two or more layers).</summary>
    public const string SeverityViolation = "Violation";

    /// <summary>Severity for single-step inversions (FR-011 "potential violation").</summary>
    public const string SeverityPotentialViolation = "PotentialViolation";

    /// <summary>Default rules used when <see cref="AceOptions.ArchitectureRules"/> is empty.</summary>
    public static readonly IReadOnlyList<ArchitectureRule> DefaultRules =
    [
        new() { Name = "layered-architecture", Layers = ["Controller", "Service", "Repository"] },
        new() { Name = "domain-independence", Layers = ["Infrastructure", "Domain"] },
    ];

    /// <summary>Edge types inspected for layer violations.</summary>
    private static readonly HashSet<EdgeType> ScannedEdgeTypes =
    [
        EdgeType.Calls,
        EdgeType.References,
        EdgeType.DependsOn,
    ];

    private readonly ILogger<ArchitectureEngine> _logger;

    public ArchitectureEngine(ILogger<ArchitectureEngine>? logger = null)
        => _logger = logger ?? NullLogger<ArchitectureEngine>.Instance;

    /// <summary>
    /// Scans the graph for architecture violations. Returns an empty list when
    /// <see cref="AceOptions.EnableArchitectureAnalysis"/> is false.
    /// </summary>
    public IReadOnlyList<ArchitectureViolation> Analyze(ICodeGraph graph, AceOptions options)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.EnableArchitectureAnalysis)
        {
            return [];
        }

        var rules = options.ArchitectureRules.Count > 0 ? options.ArchitectureRules : DefaultRules;

        var layerByNode = graph.GetNodes()
            .Select(node => (node.Id, Layer: ClassifyLayer(node)))
            .Where(entry => entry.Layer is not null)
            .ToDictionary(entry => entry.Id, entry => entry.Layer!, StringComparer.Ordinal);

        var violations = new List<ArchitectureViolation>();
        var seen = new HashSet<(string Rule, string Source, string Target, EdgeType Type)>();

        foreach (var edge in graph.GetEdges()
                     .Where(edge => ScannedEdgeTypes.Contains(edge.Type))
                     .OrderBy(edge => edge.SourceId, StringComparer.Ordinal)
                     .ThenBy(edge => edge.TargetId, StringComparer.Ordinal))
        {
            if (!layerByNode.TryGetValue(edge.SourceId, out var sourceLayer) ||
                !layerByNode.TryGetValue(edge.TargetId, out var targetLayer))
            {
                continue;
            }

            foreach (var rule in rules)
            {
                var sourceIndex = IndexOfLayer(rule, sourceLayer);
                var targetIndex = IndexOfLayer(rule, targetLayer);

                // Allowed direction is outer → inner (increasing index); same-layer and
                // inward → outward dependencies are fine, outward inversions are not.
                if (sourceIndex < 0 || targetIndex < 0 || targetIndex >= sourceIndex)
                {
                    continue;
                }

                if (!seen.Add((rule.Name, edge.SourceId, edge.TargetId, edge.Type)))
                {
                    continue;
                }

                var gap = sourceIndex - targetIndex;
                violations.Add(new ArchitectureViolation
                {
                    Rule = rule.Name,
                    Source = ImpactEngine.DisplayName(graph, edge.SourceId),
                    Target = ImpactEngine.DisplayName(graph, edge.TargetId),
                    EdgeType = edge.Type,
                    Severity = gap >= 2 ? SeverityViolation : SeverityPotentialViolation,
                    Location = edge.Location,
                    Message = $"'{sourceLayer}' depends on outer layer '{targetLayer}' ({edge.Type}); " +
                              $"rule '{rule.Name}' allows only {string.Join(" -> ", rule.Layers)}.",
                });
            }
        }

        _logger.LogDebug("Architecture analysis: {Count} violation(s) across {Rules} rule(s)", violations.Count, rules.Count);
        return violations;
    }

    /// <summary>
    /// Assigns a node to an architecture layer by name/namespace/project conventions.
    /// Returns null when no convention matches.
    /// </summary>
    public static string? ClassifyLayer(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // Type/member names carry the strongest signal.
        if (node.Name.Contains("Controller", StringComparison.OrdinalIgnoreCase))
        {
            return "Controller";
        }

        if (node.Name.Contains("Repository", StringComparison.OrdinalIgnoreCase))
        {
            return "Repository";
        }

        if (node.Name.Contains("Service", StringComparison.OrdinalIgnoreCase))
        {
            return "Service";
        }

        // Namespace/project naming for infrastructure boundaries.
        var scope = $"{node.Namespace}.{node.Project}";
        if (scope.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase))
        {
            return "Infrastructure";
        }

        if (scope.Contains("Domain", StringComparison.OrdinalIgnoreCase))
        {
            return "Domain";
        }

        return null;
    }

    private static int IndexOfLayer(ArchitectureRule rule, string layer)
        => rule.Layers.FindIndex(l => string.Equals(l, layer, StringComparison.OrdinalIgnoreCase));
}
