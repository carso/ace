using System.ComponentModel;
using Ace.Core.Services;
using ModelContextProtocol.Server;

namespace Ace.Mcp.Server.Tools;

/// <summary>Repository-level tools: indexing/analysis entry point and engine status (§8).</summary>
[McpServerToolType]
public sealed class RepositoryTools
{
    private readonly AceEngineFacade _facade;

    public RepositoryTools(AceEngineFacade facade)
    {
        _facade = facade;
    }

    [McpServerTool(Name = "ace_repository_analyze")]
    [Description(
        "Analyze a repository: discover files and projects, build or refresh the ACE index and code graph, " +
        "and return the structured repository context (file counts, languages, frameworks, build systems, test projects).")]
    public Task<string> AnalyzeRepository(
        [Description("Absolute path to the repository root to analyze.")]
        string repositoryPath,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root => await _facade.AnalyzeRepositoryAsync(root, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = "ace_status")]
    [Description(
        "Return the ACE engine and index status for a repository: API version, index/graph statistics, " +
        "analyzer versions, staleness and per-file diagnostics.")]
    public Task<string> GetStatus(
        [Description("Absolute path to the repository root to inspect.")]
        string repositoryPath,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root =>
        {
            var status = await _facade.GetStatusAsync(root, cancellationToken).ConfigureAwait(false);
            return new
            {
                apiVersion = ToolInvoker.ApiVersion,
                repositoryPath = status.RepositoryPath,
                indexed = status.Indexed,
                fileCount = status.FileCount,
                sourceFileCount = status.SourceFileCount,
                nodeCount = status.NodeCount,
                edgeCount = status.EdgeCount,
                indexVersion = status.IndexVersion,
                analyzerVersion = status.AnalyzerVersion,
                currentAnalyzerVersion = status.CurrentAnalyzerVersion,
                stale = status.Stale,
                failedFiles = status.FailedFiles,
                languages = status.Languages,
                testProjects = status.TestProjects,
                lastLoadedUtc = status.LastLoadedUtc,
            };
        });
}
