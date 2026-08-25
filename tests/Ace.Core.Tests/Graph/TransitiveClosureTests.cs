using Ace.Core.Graph;
using Ace.Core.Models;

namespace Ace.Core.Tests.Graph;

public sealed class TransitiveClosureTests
{
    /// <summary>Builds the chain a → b → c → d → e (all Calls edges) plus a References edge a → x.</summary>
    private static InMemoryCodeGraph BuildChainGraph()
    {
        var graph = new InMemoryCodeGraph();
        foreach (var id in new[] { "a", "b", "c", "d", "e", "x" })
        {
            graph.AddNode(new GraphNode { Id = id, Type = NodeType.Class, Name = id });
        }

        foreach (var (source, target) in new[] { ("a", "b"), ("b", "c"), ("c", "d"), ("d", "e") })
        {
            graph.AddEdge(new GraphEdge { SourceId = source, TargetId = target, Type = EdgeType.Calls });
        }

        graph.AddEdge(new GraphEdge { SourceId = "a", TargetId = "x", Type = EdgeType.References });
        return graph;
    }

    [Fact]
    public void TransitiveClosure_RespectsMaxDepth()
    {
        var graph = BuildChainGraph();

        var closure = graph.TransitiveClosure(["a"], [EdgeType.Calls], EdgeDirection.Outgoing, maxDepth: 2);

        Assert.Equal(0, closure["a"]);
        Assert.Equal(1, closure["b"]);
        Assert.Equal(2, closure["c"]);
        Assert.False(closure.ContainsKey("d"));
        Assert.False(closure.ContainsKey("e"));
    }

    [Fact]
    public void TransitiveClosure_RespectsMaxVisits()
    {
        var graph = BuildChainGraph();

        var closure = graph.TransitiveClosure(["a"], [EdgeType.Calls], EdgeDirection.Outgoing, maxDepth: 10, maxVisits: 3);

        Assert.Equal(3, closure.Count);
    }

    [Fact]
    public void TransitiveClosure_TraversesIncomingEdgesForDependents()
    {
        var graph = BuildChainGraph();

        var closure = graph.TransitiveClosure(["e"], [EdgeType.Calls], EdgeDirection.Incoming, maxDepth: 2);

        Assert.Equal(0, closure["e"]);
        Assert.Equal(1, closure["d"]);
        Assert.Equal(2, closure["c"]);
        Assert.False(closure.ContainsKey("b"));
    }

    [Fact]
    public void TransitiveClosure_FiltersByEdgeType()
    {
        var graph = BuildChainGraph();

        var callsOnly = graph.TransitiveClosure(["a"], [EdgeType.Calls], EdgeDirection.Outgoing, maxDepth: 1);
        Assert.False(callsOnly.ContainsKey("x"));

        var referencesOnly = graph.TransitiveClosure(["a"], [EdgeType.References], EdgeDirection.Outgoing, maxDepth: 1);
        Assert.True(referencesOnly.ContainsKey("x"));
        Assert.False(referencesOnly.ContainsKey("b"));
    }

    [Fact]
    public void TransitiveClosure_HandlesCyclesWithoutRevisiting()
    {
        var graph = new InMemoryCodeGraph();
        foreach (var id in new[] { "p", "q" })
        {
            graph.AddNode(new GraphNode { Id = id, Type = NodeType.Class, Name = id });
        }

        graph.AddEdge(new GraphEdge { SourceId = "p", TargetId = "q", Type = EdgeType.Calls });
        graph.AddEdge(new GraphEdge { SourceId = "q", TargetId = "p", Type = EdgeType.Calls });

        var closure = graph.TransitiveClosure(["p"], null, EdgeDirection.Outgoing, maxDepth: 20);

        Assert.Equal(2, closure.Count);
        Assert.Equal(0, closure["p"]);
        Assert.Equal(1, closure["q"]);
    }

    [Fact]
    public void TransitiveClosure_IgnoresUnknownSeedsAndKeepsDepthsMinimal()
    {
        var graph = BuildChainGraph();

        var closure = graph.TransitiveClosure(["missing", "c"], null, EdgeDirection.Outgoing, maxDepth: 2);

        Assert.False(closure.ContainsKey("missing")); // unknown seeds are dropped
        Assert.Equal(0, closure["c"]);
        Assert.Equal(1, closure["d"]);
        Assert.Equal(2, closure["e"]);
    }

    [Fact]
    public void GetNeighbors_FiltersByDirectionAndType()
    {
        var graph = BuildChainGraph();

        var outgoing = graph.GetNeighbors("b", [EdgeType.Calls], EdgeDirection.Outgoing);
        Assert.Single(outgoing);
        Assert.Equal("c", outgoing[0].Id);

        var incoming = graph.GetNeighbors("b", [EdgeType.Calls], EdgeDirection.Incoming);
        Assert.Single(incoming);
        Assert.Equal("a", incoming[0].Id);

        var both = graph.GetNeighbors("b", [EdgeType.Calls], EdgeDirection.Both);
        Assert.Equal(2, both.Count);
    }
}
