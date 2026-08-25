using System.ComponentModel;
using Ace.Core.Services;
using ModelContextProtocol.Server;

namespace Ace.Mcp.Server.Tools;

/// <summary>Change-set intelligence tools: impact, risk, test impact, regression, architecture (FR-006..011, §8).</summary>
[McpServerToolType]
public sealed class AnalysisTools
{
    private readonly AceEngineFacade _facade;

    public AnalysisTools(AceEngineFacade facade)
    {
        _facade = facade;
    }

    [McpServerTool(Name = "ace_impact_analyze")]
    [Description(
        "Analyze the potential impact of code changes: changed and affected components, affected projects/APIs/tests, " +
        "merged risk level/score and the evidence trail explaining each hop.")]
    public Task<string> AnalyzeImpact(
        [Description("Absolute path to the repository root.")]
        string repositoryPath,
        [Description("Changed files as repository-relative or absolute paths, e.g. [\"src/Customer.Services/CustomerService.cs\"]. Primary input; git inputs are optional add-ons.")]
        string[]? changedFiles = null,
        [Description("Optional: also take changed files from the git working tree (requires ace.enableGitAnalysis).")]
        bool useGitWorkingTree = false,
        [Description("Optional: also take changed files from 'git diff --name-only <range>', e.g. \"HEAD~1..HEAD\" (requires ace.enableGitAnalysis).")]
        string? gitDiffRange = null,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root =>
        {
            var files = await ResolveChangedFilesAsync(root, changedFiles, useGitWorkingTree, gitDiffRange, cancellationToken).ConfigureAwait(false);
            return await _facade.AnalyzeImpactAsync(root, files, cancellationToken).ConfigureAwait(false);
        });

    [McpServerTool(Name = "ace_risk_analyze")]
    [Description("Calculate a deterministic risk level and 0-100 risk score for a change set, with the weighted factors.")]
    public Task<string> AnalyzeRisk(
        [Description("Absolute path to the repository root.")]
        string repositoryPath,
        [Description("Changed files as repository-relative or absolute paths. Primary input; git inputs are optional add-ons.")]
        string[]? changedFiles = null,
        [Description("Optional: also take changed files from the git working tree (requires ace.enableGitAnalysis).")]
        bool useGitWorkingTree = false,
        [Description("Optional: also take changed files from 'git diff --name-only <range>' (requires ace.enableGitAnalysis).")]
        string? gitDiffRange = null,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root =>
        {
            var files = await ResolveChangedFilesAsync(root, changedFiles, useGitWorkingTree, gitDiffRange, cancellationToken).ConfigureAwait(false);
            return await _facade.AnalyzeRiskAsync(root, files, cancellationToken).ConfigureAwait(false);
        });

    [McpServerTool(Name = "ace_tests_affected")]
    [Description("Identify the tests potentially affected by a change set, each with the reason it was selected.")]
    public Task<string> GetAffectedTests(
        [Description("Absolute path to the repository root.")]
        string repositoryPath,
        [Description("Changed files as repository-relative or absolute paths. Primary input; git inputs are optional add-ons.")]
        string[]? changedFiles = null,
        [Description("Optional: also take changed files from the git working tree (requires ace.enableGitAnalysis).")]
        bool useGitWorkingTree = false,
        [Description("Optional: also take changed files from 'git diff --name-only <range>' (requires ace.enableGitAnalysis).")]
        string? gitDiffRange = null,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root =>
        {
            var files = await ResolveChangedFilesAsync(root, changedFiles, useGitWorkingTree, gitDiffRange, cancellationToken).ConfigureAwait(false);
            return await _facade.GetAffectedTestsAsync(root, files, cancellationToken).ConfigureAwait(false);
        });

    [McpServerTool(Name = "ace_regression_scope")]
    [Description("Recommend a regression scope for a change set: risk level, potential impact and the tests to run.")]
    public Task<string> GetRegressionScope(
        [Description("Absolute path to the repository root.")]
        string repositoryPath,
        [Description("Changed files as repository-relative or absolute paths. Primary input; git inputs are optional add-ons.")]
        string[]? changedFiles = null,
        [Description("Optional: also take changed files from the git working tree (requires ace.enableGitAnalysis).")]
        bool useGitWorkingTree = false,
        [Description("Optional: also take changed files from 'git diff --name-only <range>' (requires ace.enableGitAnalysis).")]
        string? gitDiffRange = null,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root =>
        {
            var files = await ResolveChangedFilesAsync(root, changedFiles, useGitWorkingTree, gitDiffRange, cancellationToken).ConfigureAwait(false);
            return await _facade.GetRegressionScopeAsync(root, files, cancellationToken).ConfigureAwait(false);
        });

    [McpServerTool(Name = "ace_architecture_analyze")]
    [Description(
        "Analyze the repository architecture against the configured layering rules and report violations. " +
        "Gated by ace.enableArchitectureAnalysis (default true): when disabled in ace.json the analysis is " +
        "suppressed and an empty list is returned.")]
    public Task<string> AnalyzeArchitecture(
        [Description("Absolute path to the repository root.")]
        string repositoryPath,
        CancellationToken cancellationToken = default)
        => ToolInvoker.ExecuteAsync(repositoryPath, async root => await _facade.AnalyzeArchitectureAsync(root, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Merges the explicit changedFiles (primary input) with optional git change inputs,
    /// then validates every candidate against the repository root via PathGuard.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveChangedFilesAsync(
        string root,
        string[]? changedFiles,
        bool useGitWorkingTree,
        string? gitDiffRange,
        CancellationToken cancellationToken)
    {
        var resolved = await _facade.ResolveChangedFilesAsync(
            root, changedFiles ?? [], useGitWorkingTree, gitDiffRange, cancellationToken).ConfigureAwait(false);
        return ToolInvoker.ValidateChangedFiles(root, resolved);
    }
}
