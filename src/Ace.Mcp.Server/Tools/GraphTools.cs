using System.ComponentModel;
using Ace.Core.Graph;
using Ace.Core.Models;
using Ace.Core.Services;
using ModelContextProtocol.Server;

namespace Ace.Mcp.Server.Tools;

/// <summary>Graph and dependency tools (FR-004/FR-005, §8).</summary>
[McpServerToolType]
public sealed class GraphTools
{
    private readonly AceEngineFacade _facade;

    public GraphTools(AceEngineFacade facade)
    {
        _facade = facade;
    }

    [McpServerTool(Name = "ace_dependencies_get")]
    [Description("Retrieve the outgoing dependencies of a symbol (types, projects and packages it depends on).")]
    public Task<string> GetDependencies(
        [Description("Absolute path to the repository root.")]
        string repositoryPath,
        [Description("Symbol name to resolve, e.g. \"CustomerService\".")]
        string symbol,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root => await _facade.GetDependenciesAsync(root, symbol, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "ace_graph_build")]
    [Description("Force a rebuild of the ACE code graph for the repository and persist it under the .ace index path.")]
    public Task<string> BuildGraph(
        [Description("Absolute path to the repository root.")]
        string repositoryPath,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root => await _facade.BuildGraphAsync(root, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "ace_graph_query")]
    [Description("Query the code graph for the neighbors of a node, optionally filtered by edge type and direction.")]
    public Task<string> QueryGraph(
        [Description("Absolute path to the repository root.")]
        string repositoryPath,
        [Description("Graph node id, e.g. \"Customer.Services:Customer.Services.CustomerService\".")]
        string nodeId,
        [Description("Optional edge-type filter. Valid values: Contains, References, Calls, Implements, Inherits, DependsOn, Uses, Tests, Exposes, Configures, Reads, Writes.")]
        string[]? edgeTypes = null,
        [Description("Traversal direction: \"Incoming\", \"Outgoing\" or \"Both\" (default).")]
        string? direction = null,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root => await _facade.QueryGraphAsync(
            root, nodeId, ParseEdgeTypes(edgeTypes), ParseDirection(direction), cancellationToken).ConfigureAwait(false));

    private static IReadOnlyCollection<EdgeType>? ParseEdgeTypes(string[]? edgeTypes)
    {
        if (edgeTypes is not { Length: > 0 })
        {
            return null;
        }

        var parsed = new List<EdgeType>(edgeTypes.Length);
        foreach (var value in edgeTypes)
        {
            if (!Enum.TryParse<EdgeType>(value, ignoreCase: true, out var type))
            {
                throw new ArgumentException(
                    $"Unknown edgeType '{value}'. Valid values: {string.Join(", ", Enum.GetNames<EdgeType>())}.");
            }

            parsed.Add(type);
        }

        return parsed;
    }

    private static EdgeDirection ParseDirection(string? direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            return EdgeDirection.Both;
        }

        if (!Enum.TryParse<EdgeDirection>(direction, ignoreCase: true, out var parsed))
        {
            throw new ArgumentException(
                $"Unknown direction '{direction}'. Valid values: Incoming, Outgoing, Both.");
        }

        return parsed;
    }
}
