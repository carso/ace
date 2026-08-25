using Ace.Core.Models;

namespace Ace.Core.Tests;

/// <summary>Scaffold smoke test: the Ace.Core assembly loads and exposes the core model surface.</summary>
public class SmokeTests
{
    [Fact]
    public void CoreAssembly_ExposesFoundationalModels()
    {
        var assembly = typeof(GraphNode).Assembly;

        Assert.Equal("Ace.Core", assembly.GetName().Name);
        Assert.Equal(17, Enum.GetValues<NodeType>().Length);
        Assert.Equal(12, Enum.GetValues<EdgeType>().Length);
        Assert.Equal(3, Enum.GetValues<Confidence>().Length);
    }
}
