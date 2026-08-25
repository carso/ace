using Ace.Core.Graph;
using Ace.Core.Models;

namespace Ace.Core.Engines;

/// <summary>One located symbol: where it lives and what it is.</summary>
public sealed record SymbolLocation
{
    /// <summary>Graph node id, e.g. "Customer.Services:Customer.Services.CustomerService".</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>Symbol kind, e.g. "Class", "Interface", "Method".</summary>
    public string Kind { get; init; } = string.Empty;

    public string? Project { get; init; }

    /// <summary>Repository-relative file path, when the symbol maps to a file.</summary>
    public string? FilePath { get; init; }

    /// <summary>1-based declaration line, when the analyzer recorded one.</summary>
    public int? Line { get; init; }
}

/// <summary>
/// Code search over the graph symbol index: case-insensitive substring matching on
/// node names/ids, returning symbol locations (file, line, project, kind).
/// </summary>
public sealed class CodeSearchService
{
    /// <summary>Finds symbols whose name or id contains <paramref name="query"/> (case-insensitive).</summary>
    public IReadOnlyList<SymbolLocation> Search(ICodeGraph graph, string query)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return graph.FindNodesByName(query.Trim())
            .Select(ToLocation)
            .ToList();
    }

    private static SymbolLocation ToLocation(GraphNode node)
    {
        var kind = node.Type switch
        {
            NodeType.Class or NodeType.Interface or NodeType.Record
                => GraphMeta.GetString(node, "kind") ?? node.Type.ToString(),
            NodeType.Method => GraphMeta.GetString(node, "memberKind") ?? node.Type.ToString(),
            _ => node.Type.ToString(),
        };

        return new SymbolLocation
        {
            Id = node.Id,
            Name = node.Name,
            Kind = kind,
            Project = node.Project,
            FilePath = node.FilePath,
            Line = GraphMeta.GetInt(node, "startLine"),
        };
    }
}
