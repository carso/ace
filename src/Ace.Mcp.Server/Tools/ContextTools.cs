using System.ComponentModel;
using Ace.Core.Engines;
using Ace.Core.Services;
using ModelContextProtocol.Server;

namespace Ace.Mcp.Server.Tools;

/// <summary>Context retrieval and code search tools (FR-012, §8).</summary>
[McpServerToolType]
public sealed class ContextTools
{
    private readonly AceEngineFacade _facade;

    public ContextTools(AceEngineFacade facade)
    {
        _facade = facade;
    }

    [McpServerTool(Name = "ace_context_get")]
    [Description(
        "Retrieve prioritized context for a symbol or file query: directly relevant code first, then " +
        "dependencies, impacted components, related tests, configuration, architecture and repository context.")]
    public Task<string> GetContext(
        [Description("Absolute path to the repository root.")]
        string repositoryPath,
        [Description("Symbol, type, member or file to gather context for, e.g. \"CustomerService\".")]
        string query,
        [Description("Maximum number of context items to return (default 50).")]
        int? maxItems = null,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root => await _facade.GetContextAsync(
            root, query, maxItems ?? ContextEngine.DefaultMaxItems, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "ace_code_search")]
    [Description("Search code symbols in the repository graph by name (case-insensitive substring match).")]
    public Task<string> SearchCode(
        [Description("Absolute path to the repository root.")]
        string repositoryPath,
        [Description("Symbol name or substring to search for.")]
        string query,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root => await _facade.SearchCodeAsync(root, query, cancellationToken).ConfigureAwait(false));
}
