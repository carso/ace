using Ace.Core.Graph;
using Ace.Core.Models;
using Ace.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ace.Core.Engines;

/// <summary>
/// Detailed impact result: the public <see cref="ImpactReport"/> plus the raw closure
/// facts (per-node depth) that downstream engines (risk, test impact, regression) reuse.
/// </summary>
public sealed record ImpactAnalysis
{
    public required ImpactReport Report { get; init; }

    /// <summary>Closure node id → hop distance from the nearest changed component (seeds are 0).</summary>
    public required IReadOnlyDictionary<string, int> Closure { get; init; }

    /// <summary>Graph node ids declared in the changed files (the traversal seeds).</summary>
    public required IReadOnlyList<string> ChangedNodeIds { get; init; }

    /// <summary>Validated, repository-relative changed file paths (forward slashes).</summary>
    public required IReadOnlyList<string> ChangedFiles { get; init; }
}

/// <summary>
/// Change impact engine (FR-006/FR-007). Maps changed files to the graph nodes declared
/// in them, then runs a bounded reverse traversal ("who depends on what changed?") over
/// CALLS/REFERENCES/IMPLEMENTS/INHERITS/DEPENDS_ON/TESTS with a depth cap of 3 and a
/// visit cap of 10,000. Produces the §9 report shape with direct/indirect split,
/// affected projects/APIs/tests and evidence links traced along the actual edge chain.
/// Risk fields are placeholders (0/Low) until the risk engine scores them.
/// </summary>
public sealed class ImpactEngine
{
    /// <summary>Maximum hop distance for the reverse traversal.</summary>
    public const int MaxDepth = 3;

    /// <summary>Maximum number of visited nodes before the closure is truncated.</summary>
    public const int MaxVisits = 10_000;

    /// <summary>Upper bound on evidence links emitted per analysis.</summary>
    public const int MaxEvidenceLinks = 500;

    /// <summary>Edge types traversed for impact propagation.</summary>
    public static readonly IReadOnlyCollection<EdgeType> ImpactEdgeTypes =
    [
        EdgeType.Calls,
        EdgeType.References,
        EdgeType.Implements,
        EdgeType.Inherits,
        EdgeType.DependsOn,
        EdgeType.Tests,
    ];

    /// <summary>Node types that can act as change seeds (declared code, not structure).</summary>
    private static readonly HashSet<NodeType> SeedNodeTypes =
    [
        NodeType.Class,
        NodeType.Interface,
        NodeType.Record,
        NodeType.Method,
        NodeType.Test,
        NodeType.Api,
    ];

    /// <summary>Node types considered "components" in reports (excludes structural nodes).</summary>
    private static readonly HashSet<NodeType> ComponentNodeTypes =
    [
        NodeType.Class,
        NodeType.Interface,
        NodeType.Record,
        NodeType.Method,
        NodeType.Test,
        NodeType.Api,
        NodeType.Database,
        NodeType.StoredProcedure,
    ];

    /// <summary>Relationship names for reverse traversal (SRS §9 style: "called-by").</summary>
    private static readonly Dictionary<EdgeType, string> ReverseRelationships = new()
    {
        [EdgeType.Calls] = "called-by",
        [EdgeType.References] = "referenced-by",
        [EdgeType.Implements] = "implemented-by",
        [EdgeType.Inherits] = "inherited-by",
        [EdgeType.DependsOn] = "depended-on-by",
        [EdgeType.Tests] = "tested-by",
    };

    private readonly ILogger<ImpactEngine> _logger;

    public ImpactEngine(ILogger<ImpactEngine>? logger = null)
        => _logger = logger ?? NullLogger<ImpactEngine>.Instance;

    /// <summary>Analyzes the impact of a set of changed files; see <see cref="AnalyzeDetailed"/>.</summary>
    public ImpactReport Analyze(ICodeGraph graph, string repositoryPath, IEnumerable<string> changedFiles)
        => AnalyzeDetailed(graph, repositoryPath, changedFiles).Report;

    /// <summary>
    /// Validates <paramref name="changedFiles"/> against <paramref name="repositoryPath"/>
    /// (PathGuard), maps them to declared graph nodes and computes the reverse impact closure.
    /// </summary>
    /// <exception cref="PathSecurityException">A changed file escapes the repository root.</exception>
    public ImpactAnalysis AnalyzeDetailed(ICodeGraph graph, string repositoryPath, IEnumerable<string> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var relativePaths = NormalizeChangedFiles(repositoryPath, changedFiles);
        var seedIds = MapChangedFilesToNodes(graph, relativePaths);

        var closure = graph.TransitiveClosure(seedIds, ImpactEdgeTypes, EdgeDirection.Incoming, MaxDepth, MaxVisits);

        var affected = closure
            .Where(kv => kv.Value > 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        var changedComponents = seedIds.Select(id => DisplayName(graph, id)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var directComponents = new List<string>();
        var indirectComponents = new List<string>();
        var affectedProjects = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var affectedApis = new List<string>();
        var affectedTests = new List<string>();
        var maxDepthReached = 0;

        foreach (var (nodeId, depth) in affected)
        {
            if (!graph.TryGetNode(nodeId, out var node) || node is null)
            {
                continue;
            }

            maxDepthReached = Math.Max(maxDepthReached, depth);

            if (node.Project is { Length: > 0 } project)
            {
                affectedProjects.Add(project);
            }

            if (!ComponentNodeTypes.Contains(node.Type))
            {
                continue;
            }

            var displayName = DisplayName(graph, nodeId);
            if (depth == 1)
            {
                directComponents.Add(displayName);
            }
            else
            {
                indirectComponents.Add(displayName);
            }

            if (node.Type == NodeType.Test)
            {
                affectedTests.Add(displayName);
            }

            if (IsApiSurface(node, displayName))
            {
                affectedApis.Add(displayName);
            }
        }

        // Projects owning the changed components are part of the blast radius too.
        foreach (var seedId in seedIds)
        {
            if (graph.TryGetNode(seedId, out var seedNode) && seedNode?.Project is { Length: > 0 } seedProject)
            {
                affectedProjects.Add(seedProject);
            }
        }

        var evidence = TraceEvidence(graph, closure, affected);
        var truncated = closure.Count >= MaxVisits;

        var report = new ImpactReport
        {
            // Placeholder: the risk engine fills these (facade merges them).
            RiskLevel = RiskLevel.Low,
            RiskScore = 0,
            ChangedComponents = changedComponents,
            AffectedComponents = directComponents.Concat(indirectComponents).Distinct(StringComparer.Ordinal).ToList(),
            DirectAffectedComponents = directComponents.Distinct(StringComparer.Ordinal).ToList(),
            IndirectAffectedComponents = indirectComponents.Distinct(StringComparer.Ordinal).ToList(),
            AffectedProjects = affectedProjects.ToList(),
            AffectedApis = affectedApis.Distinct(StringComparer.Ordinal).ToList(),
            AffectedTests = affectedTests.Distinct(StringComparer.Ordinal).ToList(),
            MaxDepthReached = maxDepthReached,
            Evidence = evidence,
            Truncated = truncated,
        };

        _logger.LogDebug(
            "Impact analysis: {ChangedFiles} file(s) -> {Changed} changed component(s), {Affected} affected, truncated={Truncated}",
            relativePaths.Count, changedComponents.Count, report.AffectedComponents.Count, truncated);

        return new ImpactAnalysis
        {
            Report = report,
            Closure = closure,
            ChangedNodeIds = seedIds,
            ChangedFiles = relativePaths,
        };
    }

    /// <summary>
    /// Validates and normalizes changed file paths to repository-relative forward-slash
    /// form. Absolute paths must live inside the repository root (PathGuard).
    /// </summary>
    /// <exception cref="PathSecurityException">A path escapes the repository root.</exception>
    public static IReadOnlyList<string> NormalizeChangedFiles(string repositoryPath, IEnumerable<string> changedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var root = Path.GetFullPath(repositoryPath);
        var normalized = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            var validated = PathGuard.EnsureWithinRoot(root, file.Trim());
            normalized.Add(Path.GetRelativePath(root, validated).Replace('\\', '/'));
        }

        return normalized.ToList();
    }

    /// <summary>Maps repository-relative file paths to the graph nodes declared in them.</summary>
    public static IReadOnlyList<string> MapChangedFilesToNodes(ICodeGraph graph, IReadOnlyCollection<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(relativePaths);

        var paths = new HashSet<string>(relativePaths, StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
        {
            return [];
        }

        return graph.GetNodes()
            .Where(node => node.FilePath is not null && paths.Contains(node.FilePath) && SeedNodeTypes.Contains(node.Type))
            .Select(node => node.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Human-friendly display name for a node ("CustomerService.CalculateDiscount").</summary>
    public static string DisplayName(ICodeGraph graph, string nodeId)
    {
        if (!graph.TryGetNode(nodeId, out var node) || node is null)
        {
            return nodeId;
        }

        if (node.Type == NodeType.Method)
        {
            var qualified = nodeId.Contains(':') ? nodeId[(nodeId.IndexOf(':') + 1)..] : nodeId;
            var hashIndex = qualified.IndexOf('#');
            var typeName = hashIndex >= 0 ? qualified[..hashIndex] : qualified;
            var dotIndex = typeName.LastIndexOf('.');
            if (dotIndex >= 0)
            {
                typeName = typeName[(dotIndex + 1)..];
            }

            return typeName.Length > 0 ? $"{typeName}.{node.Name}" : node.Name;
        }

        return node.Name.Length > 0 ? node.Name : nodeId;
    }

    /// <summary>
    /// Traces evidence links along the actual edge chain from each affected component
    /// back toward the changed seeds: one link per hop, in "called-by" style.
    /// </summary>
    private static List<EvidenceLink> TraceEvidence(
        ICodeGraph graph,
        IReadOnlyDictionary<string, int> closure,
        IReadOnlyCollection<KeyValuePair<string, int>> affected)
    {
        // Impact edges point from the dependent (deeper) node to what it depends on
        // (shallower), so walking back toward the seeds follows outgoing edges.
        var outgoingBySource = graph.GetEdges()
            .Where(edge => ImpactEdgeTypes.Contains(edge.Type))
            .GroupBy(edge => edge.SourceId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(edge => edge.TargetId, StringComparer.Ordinal)
                    .ThenBy(edge => edge.Type)
                    .ToList(),
                StringComparer.Ordinal);

        var links = new List<EvidenceLink>();

        foreach (var (nodeId, depth) in affected)
        {
            var currentId = nodeId;
            var currentDepth = depth;

            while (currentDepth > 0 && links.Count < MaxEvidenceLinks)
            {
                GraphEdge? step = null;
                if (outgoingBySource.TryGetValue(currentId, out var candidates))
                {
                    // Follow an edge whose target sits exactly one hop closer to the seeds.
                    step = candidates.FirstOrDefault(edge =>
                        closure.TryGetValue(edge.TargetId, out var targetDepth) && targetDepth == currentDepth - 1);
                }

                if (step is null)
                {
                    break;
                }

                links.Add(new EvidenceLink
                {
                    Source = DisplayName(graph, step.TargetId),
                    Relationship = ReverseRelationships.GetValueOrDefault(step.Type, step.Type.ToString().ToLowerInvariant()),
                    Target = DisplayName(graph, currentId),
                });

                currentId = step.TargetId;
                currentDepth--;
            }
        }

        return links;
    }

    private static bool IsApiSurface(GraphNode node, string displayName)
        => node.Type == NodeType.Api
            || displayName.Contains("Controller", StringComparison.OrdinalIgnoreCase);
}
