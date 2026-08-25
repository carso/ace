using Ace.Core.Graph;
using Ace.Core.Indexing;
using Ace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ace.Core.Engines;

/// <summary>
/// Prioritized context retrieval (FR-012). Given a symbol or file query, assembles
/// context in 7 tiers — 1 direct code, 2 dependencies, 3 impacted components,
/// 4 related tests, 5 configuration, 6 architecture, 7 repository context — and
/// enforces a maxItems budget with an explicit truncation marker.
/// </summary>
public sealed class ContextEngine
{
    /// <summary>Default item budget for a context response.</summary>
    public const int DefaultMaxItems = 50;

    /// <summary>Tier used by the truncation marker item (after the 7 content tiers).</summary>
    public const int TruncationTier = 8;

    /// <summary>Reverse-closure depth used for tier 3 (impacted components).</summary>
    private const int ImpactedTierDepth = 2;

    /// <summary>Dependency edge types for tier 2 (everything except structural CONTAINS).</summary>
    private static readonly EdgeType[] DependencyEdgeTypes =
    [
        EdgeType.References,
        EdgeType.Calls,
        EdgeType.Implements,
        EdgeType.Inherits,
        EdgeType.DependsOn,
        EdgeType.Uses,
        EdgeType.Tests,
        EdgeType.Exposes,
        EdgeType.Configures,
        EdgeType.Reads,
        EdgeType.Writes,
    ];

    private readonly ILogger<ContextEngine> _logger;

    public ContextEngine(ILogger<ContextEngine>? logger = null)
        => _logger = logger ?? NullLogger<ContextEngine>.Instance;

    /// <summary>
    /// Builds the prioritized context for <paramref name="query"/> (a symbol name or a
    /// repository-relative file path). <paramref name="index"/> enables the configuration
    /// tier; <paramref name="violations"/> enables the architecture tier.
    /// </summary>
    public IReadOnlyList<ContextItem> GetContext(
        ICodeGraph graph,
        string query,
        RepositoryIndex? index = null,
        IReadOnlyList<ArchitectureViolation>? violations = null,
        int maxItems = DefaultMaxItems)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        maxItems = Math.Max(1, maxItems);

        var targets = ResolveTargets(graph, query);
        var items = new List<ContextItem>();
        var included = new HashSet<string>(StringComparer.Ordinal);

        void Add(int tier, string key, string title, double score, string? path, string reason)
        {
            if (!included.Add($"{tier}|{key}"))
            {
                return;
            }

            items.Add(new ContextItem { Tier = tier, Title = title, Score = score, Path = path, Reason = reason });
        }

        // ---- Tier 1: direct code (the symbol/file and its declarations) ----
        var targetFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            Add(1, target.Id, ImpactEngine.DisplayName(graph, target.Id), 100.0, target.FilePath, "direct match for query");
            if (target.FilePath is { Length: > 0 })
            {
                targetFiles.Add(target.FilePath);
            }
        }

        if (targets.Count == 0 && LooksLikeFilePath(query))
        {
            targetFiles.Add(query.Replace('\\', '/'));
            Add(1, $"file:{query}", query, 100.0, query.Replace('\\', '/'), "queried file");
        }

        foreach (var node in graph.GetNodes()
                     .Where(node => node.FilePath is not null && targetFiles.Contains(node.FilePath))
                     .OrderBy(node => node.Id, StringComparer.Ordinal))
        {
            Add(1, node.Id, ImpactEngine.DisplayName(graph, node.Id), 90.0, node.FilePath, "declared in the queried file");
        }

        // ---- Tier 2: dependencies (outgoing edges of the targets) ----
        var targetIds = targets.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var targetId in targetIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            foreach (var neighbor in graph.GetNeighbors(targetId, DependencyEdgeTypes, EdgeDirection.Outgoing))
            {
                if (neighbor.Type == NodeType.Namespace || targetIds.Contains(neighbor.Id))
                {
                    continue;
                }

                Add(2, neighbor.Id, ImpactEngine.DisplayName(graph, neighbor.Id), 80.0, neighbor.FilePath,
                    $"dependency of {ImpactEngine.DisplayName(graph, targetId)}");
            }
        }

        // ---- Tier 3: impacted components (reverse closure, depth <= 2) ----
        var closure = graph.TransitiveClosure(targetIds, ImpactEngine.ImpactEdgeTypes, EdgeDirection.Incoming, ImpactedTierDepth);
        foreach (var (nodeId, depth) in closure
                     .Where(kv => kv.Value > 0)
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!graph.TryGetNode(nodeId, out var node) || node is null || node.Type == NodeType.Namespace)
            {
                continue;
            }

            Add(3, nodeId, ImpactEngine.DisplayName(graph, nodeId), 70.0 - (depth * 5.0), node.FilePath,
                $"depends on the queried code (impact depth {depth})");
        }

        // ---- Tier 4: related tests ----
        var closureIds = closure.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var edge in graph.GetEdges()
                     .Where(edge => edge.Type == EdgeType.Tests && (targetIds.Contains(edge.TargetId) || closureIds.Contains(edge.TargetId)))
                     .OrderBy(edge => edge.SourceId, StringComparer.Ordinal))
        {
            if (graph.TryGetNode(edge.SourceId, out var testNode) && testNode is not null)
            {
                Add(4, edge.SourceId, ImpactEngine.DisplayName(graph, edge.SourceId), 55.0, testNode.FilePath,
                    $"tests {ImpactEngine.DisplayName(graph, edge.TargetId)}");
            }
        }

        foreach (var (nodeId, _) in closure.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (graph.TryGetNode(nodeId, out var node) && node is { Type: NodeType.Test })
            {
                Add(4, nodeId, ImpactEngine.DisplayName(graph, nodeId), 50.0, node.FilePath, "test in impact closure");
            }
        }

        // ---- Tier 5: configuration files ----
        if (index is not null)
        {
            foreach (var (relativePath, entry) in index.Files
                         .Where(kv => string.Equals(kv.Value.Category, "config", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var sameDirectory = targetFiles.Any(file =>
                    DirectoryOf(file).Length > 0 &&
                    string.Equals(DirectoryOf(relativePath), DirectoryOf(file), StringComparison.OrdinalIgnoreCase));

                Add(5, relativePath, relativePath, sameDirectory ? 50.0 : 40.0, relativePath,
                    sameDirectory ? "configuration next to the queried code" : "repository configuration");
            }
        }

        // ---- Tier 6: architecture information ----
        if (violations is { Count: > 0 })
        {
            var targetNames = targets.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var relevant = targetNames.Count > 0
                ? violations.Where(v => targetNames.Contains(SimpleName(v.Source)) || targetNames.Contains(SimpleName(v.Target))).ToList()
                : [];

            foreach (var violation in (relevant.Count > 0 ? relevant : violations).Take(10))
            {
                Add(6, $"{violation.Rule}|{violation.Source}|{violation.Target}",
                    $"{violation.Rule}: {violation.Source} -> {violation.Target}", 35.0, violation.Location,
                    $"architecture {violation.Severity.ToLowerInvariant()}");
            }
        }

        // ---- Tier 7: broader repository context ----
        var projectCount = graph.GetNodes().Count(node => node.Type == NodeType.Project);
        Add(7, "repository-summary",
            $"Repository graph: {projectCount} project(s), {graph.GetNodes().Count} node(s), {graph.GetEdges().Count} edge(s)",
            25.0, null, "repository overview");

        var ordered = items
            .OrderBy(item => item.Tier)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToList();

        if (ordered.Count <= maxItems)
        {
            return ordered;
        }

        var kept = ordered.Take(maxItems - 1).ToList();
        kept.Add(new ContextItem
        {
            Tier = TruncationTier,
            Title = $"[truncated] {ordered.Count - kept.Count} more context item(s) available; refine the query or raise maxItems",
            Score = 0,
            Reason = "context budget exhausted",
        });

        _logger.LogDebug("Context for '{Query}': {Kept}/{Total} items (budget {Budget})", query, kept.Count, ordered.Count, maxItems);
        return kept;
    }

    private static IReadOnlyList<GraphNode> ResolveTargets(ICodeGraph graph, string query)
    {
        // Prefer exact symbol-name matches, then file-path matches, then substring matches.
        var byName = graph.FindNodesByName(query);
        var exact = byName
            .Where(node => string.Equals(node.Name, query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToList();

        if (exact.Count > 0)
        {
            return exact;
        }

        var normalizedQuery = query.Replace('\\', '/');
        var byFile = graph.GetNodes()
            .Where(node => node.FilePath is not null &&
                           string.Equals(node.FilePath, normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToList();

        if (byFile.Count > 0)
        {
            return byFile;
        }

        return byName
            .Where(node => node.Type != NodeType.Namespace)
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .Take(5)
            .ToList();
    }

    private static bool LooksLikeFilePath(string query)
        => query.Contains('/') || query.Contains('\\') || Path.HasExtension(query);

    private static string DirectoryOf(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index >= 0 ? normalized[..index] : string.Empty;
    }

    private static string SimpleName(string displayName)
    {
        var dot = displayName.LastIndexOf('.');
        return dot >= 0 ? displayName[(dot + 1)..] : displayName;
    }
}
