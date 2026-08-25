using Ace.Core.Models;

namespace Ace.Core.Graph;

/// <summary>Direction to traverse edges from a node.</summary>
public enum EdgeDirection
{
    /// <summary>Edges where the node is the source (dependencies of the node).</summary>
    Outgoing,

    /// <summary>Edges where the node is the target (dependents of the node).</summary>
    Incoming,

    /// <summary>Edges in either direction.</summary>
    Both,
}

/// <summary>
/// The ACE code graph abstraction, independent of any specific storage (SRS §10, FR-005).
/// Node IDs are stable strings, e.g. "Customer.Services:Customer.Services.CustomerService#CalculateDiscount".
/// </summary>
public interface ICodeGraph
{
    void AddNode(GraphNode node);

    void AddEdge(GraphEdge edge);

    bool TryGetNode(string id, out GraphNode? node);

    /// <summary>Returns the node with the given id or throws when absent.</summary>
    GraphNode GetNode(string id);

    IReadOnlyCollection<GraphNode> GetNodes();

    IReadOnlyCollection<GraphEdge> GetEdges();

    /// <summary>
    /// Neighbor nodes of <paramref name="nodeId"/>, optionally filtered by edge types
    /// and traversal direction.
    /// </summary>
    IReadOnlyList<GraphNode> GetNeighbors(
        string nodeId,
        IReadOnlyCollection<EdgeType>? edgeTypes = null,
        EdgeDirection direction = EdgeDirection.Both);

    /// <summary>Case-insensitive substring match over node names and ids.</summary>
    IReadOnlyList<GraphNode> FindNodesByName(string query);

    /// <summary>
    /// Bounded BFS over the graph from the seed nodes. Returns every visited node id
    /// with the depth at which it was first reached (seeds are depth 0). Traversal stops
    /// expanding past <paramref name="maxDepth"/> and visits at most <paramref name="maxVisits"/> nodes.
    /// </summary>
    IReadOnlyDictionary<string, int> TransitiveClosure(
        IEnumerable<string> seedIds,
        IReadOnlyCollection<EdgeType>? edgeTypes = null,
        EdgeDirection direction = EdgeDirection.Outgoing,
        int maxDepth = 3,
        int maxVisits = 10_000);
}
