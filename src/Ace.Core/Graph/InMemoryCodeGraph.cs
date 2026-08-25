using Ace.Core.Models;

namespace Ace.Core.Graph;

/// <summary>
/// Dictionary-backed in-memory code graph with forward AND reverse adjacency
/// for O(1) neighbor lookup in either direction.
/// </summary>
public sealed class InMemoryCodeGraph : ICodeGraph
{
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<GraphEdge> _edges = [];
    private readonly Dictionary<string, List<AdjacentEdge>> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AdjacentEdge>> _incoming = new(StringComparer.Ordinal);

    private readonly record struct AdjacentEdge(string OtherNodeId, EdgeType Type);

    public void AddNode(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(node.Id);
        _nodes.TryAdd(node.Id, node);
    }

    public void AddEdge(GraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        _edges.Add(edge);

        AddAdjacency(_outgoing, edge.SourceId, new AdjacentEdge(edge.TargetId, edge.Type));
        AddAdjacency(_incoming, edge.TargetId, new AdjacentEdge(edge.SourceId, edge.Type));
    }

    public bool TryGetNode(string id, out GraphNode? node) => _nodes.TryGetValue(id, out node);

    public GraphNode GetNode(string id)
        => _nodes.TryGetValue(id, out var node)
            ? node
            : throw new KeyNotFoundException($"Graph node not found: {id}");

    public IReadOnlyCollection<GraphNode> GetNodes() => _nodes.Values;

    public IReadOnlyCollection<GraphEdge> GetEdges() => _edges;

    public IReadOnlyList<GraphNode> GetNeighbors(
        string nodeId,
        IReadOnlyCollection<EdgeType>? edgeTypes = null,
        EdgeDirection direction = EdgeDirection.Both)
    {
        var results = new List<GraphNode>();
        CollectNeighbors(_outgoing, nodeId, edgeTypes, direction is EdgeDirection.Outgoing or EdgeDirection.Both, results);
        CollectNeighbors(_incoming, nodeId, edgeTypes, direction is EdgeDirection.Incoming or EdgeDirection.Both, results);
        return results;
    }

    public IReadOnlyList<GraphNode> FindNodesByName(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return _nodes.Values
            .Where(n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        n.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyDictionary<string, int> TransitiveClosure(
        IEnumerable<string> seedIds,
        IReadOnlyCollection<EdgeType>? edgeTypes = null,
        EdgeDirection direction = EdgeDirection.Outgoing,
        int maxDepth = 3,
        int maxVisits = 10_000)
    {
        ArgumentNullException.ThrowIfNull(seedIds);
        maxDepth = Math.Max(0, maxDepth);
        maxVisits = Math.Max(1, maxVisits);

        var visited = new Dictionary<string, int>(StringComparer.Ordinal);
        var frontier = new List<string>();

        foreach (var seed in seedIds)
        {
            // Seeds that are not part of the graph are ignored.
            if (seed.Length > 0 && _nodes.ContainsKey(seed) && visited.TryAdd(seed, 0))
            {
                frontier.Add(seed);
                if (visited.Count >= maxVisits)
                {
                    return visited;
                }
            }
        }

        for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var nodeId in frontier)
            {
                foreach (var neighborId in AdjacentIds(nodeId, edgeTypes, direction))
                {
                    if (!_nodes.ContainsKey(neighborId) || !visited.TryAdd(neighborId, depth))
                    {
                        continue;
                    }

                    next.Add(neighborId);
                    if (visited.Count >= maxVisits)
                    {
                        return visited;
                    }
                }
            }

            frontier = next;
        }

        return visited;
    }

    private IEnumerable<string> AdjacentIds(string nodeId, IReadOnlyCollection<EdgeType>? edgeTypes, EdgeDirection direction)
    {
        if ((direction is EdgeDirection.Outgoing or EdgeDirection.Both) &&
            _outgoing.TryGetValue(nodeId, out var outgoing))
        {
            foreach (var edge in outgoing.Where(e => edgeTypes is null || edgeTypes.Contains(e.Type)))
            {
                yield return edge.OtherNodeId;
            }
        }

        if ((direction is EdgeDirection.Incoming or EdgeDirection.Both) &&
            _incoming.TryGetValue(nodeId, out var incoming))
        {
            foreach (var edge in incoming.Where(e => edgeTypes is null || edgeTypes.Contains(e.Type)))
            {
                yield return edge.OtherNodeId;
            }
        }
    }

    private void CollectNeighbors(
        Dictionary<string, List<AdjacentEdge>> adjacency,
        string nodeId,
        IReadOnlyCollection<EdgeType>? edgeTypes,
        bool include,
        List<GraphNode> results)
    {
        if (!include || !adjacency.TryGetValue(nodeId, out var edges))
        {
            return;
        }

        foreach (var edge in edges)
        {
            if (edgeTypes is not null && !edgeTypes.Contains(edge.Type))
            {
                continue;
            }

            if (_nodes.TryGetValue(edge.OtherNodeId, out var node))
            {
                results.Add(node);
            }
        }
    }

    private static void AddAdjacency(
        Dictionary<string, List<AdjacentEdge>> adjacency,
        string nodeId,
        AdjacentEdge edge)
    {
        if (!adjacency.TryGetValue(nodeId, out var list))
        {
            list = [];
            adjacency[nodeId] = list;
        }

        list.Add(edge);
    }
}
