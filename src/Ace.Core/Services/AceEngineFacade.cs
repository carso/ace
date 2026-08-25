using System.Collections.Concurrent;
using System.Diagnostics;
using Ace.Core.Configuration;
using Ace.Core.Discovery;
using Ace.Core.Engines;
using Ace.Core.Graph;
using Ace.Core.Indexing;
using Ace.Core.Models;
using Ace.Core.Parsing;
using Ace.Core.Parsing.CSharp;
using Ace.Core.Platform;
using Ace.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ace.Core.Services;

/// <summary>Index/graph/engine status for one repository (§17 status exposure).</summary>
public sealed record AceStatus
{
    /// <summary>ACE API contract version label shared by every adapter (SRS §21).</summary>
    public const string ApiContractVersion = "ACE v1";

    /// <summary>API contract version exposed in status output (SRS §21).</summary>
    public string ApiVersion { get; init; } = ApiContractVersion;

    public required string RepositoryPath { get; init; }

    public bool Indexed { get; init; }

    public int FileCount { get; init; }

    public int SourceFileCount { get; init; }

    public int NodeCount { get; init; }

    public int EdgeCount { get; init; }

    public int IndexVersion { get; init; }

    /// <summary>Analyzer version recorded in the persisted index.</summary>
    public required string AnalyzerVersion { get; init; }

    /// <summary>Analyzer version of the running engine.</summary>
    public required string CurrentAnalyzerVersion { get; init; }

    /// <summary>True when persisted index/graph artifacts existed but were out of date at load time.</summary>
    public bool Stale { get; init; }

    /// <summary>Files that failed hashing/parsing (never abort the run, §17).</summary>
    public IReadOnlyDictionary<string, string> FailedFiles { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Languages { get; init; } = [];

    public IReadOnlyList<string> TestProjects { get; init; } = [];

    public DateTime LastLoadedUtc { get; init; }
}

/// <summary>Result of an explicit graph build.</summary>
public sealed record GraphBuildInfo
{
    public int NodeCount { get; init; }

    public int EdgeCount { get; init; }

    public long DurationMs { get; init; }

    /// <summary>Where the graph was persisted (repository-relative convention: .ace/graph.json).</summary>
    public required string PersistedPath { get; init; }
}

/// <summary>
/// The single seam between ACE intelligence and its adapters (MCP server, CLI).
/// Per repository it: validates paths via PathGuard, loads options, ensures the
/// persisted index/graph exist and are fresh (discovery + incremental index + graph
/// rebuild when stale), then dispatches to the analysis engines. Loaded sessions are
/// cached in memory so repeated tool calls are sub-second.
/// </summary>
public sealed class AceEngineFacade
{
    private readonly IFileSystemService _fileSystem;
    private readonly ILogger<AceEngineFacade> _logger;
    private readonly JsonGraphStore _graphStore;
    private readonly IGitService _gitService;

    private readonly ImpactEngine _impactEngine;
    private readonly RiskEngine _riskEngine;
    private readonly TestImpactEngine _testImpactEngine;
    private readonly RegressionEngine _regressionEngine;
    private readonly ArchitectureEngine _architectureEngine;
    private readonly ContextEngine _contextEngine;
    private readonly CodeSearchService _searchService = new();

    private readonly ConcurrentDictionary<string, Lazy<Task<RepoSession>>> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly EdgeType[] DependencyEdgeTypes =
    [
        EdgeType.References,
        EdgeType.Calls,
        EdgeType.Implements,
        EdgeType.Inherits,
        EdgeType.DependsOn,
        EdgeType.Uses,
    ];

    public AceEngineFacade(IFileSystemService? fileSystem = null, ILogger<AceEngineFacade>? logger = null, IGitService? gitService = null)
    {
        _fileSystem = fileSystem ?? new FileSystemService();
        _logger = logger ?? NullLogger<AceEngineFacade>.Instance;
        _graphStore = new JsonGraphStore(_fileSystem);
        _gitService = gitService ?? new GitService(new ProcessService());

        _impactEngine = new ImpactEngine();
        _riskEngine = new RiskEngine();
        _testImpactEngine = new TestImpactEngine(_impactEngine);
        _regressionEngine = new RegressionEngine(_impactEngine, _riskEngine, _testImpactEngine);
        _architectureEngine = new ArchitectureEngine();
        _contextEngine = new ContextEngine();
    }

    // ------------------------------------------------------------- repository

    /// <summary>
    /// Analyzes (ensures indexed) a repository and returns its structured context.
    /// Re-runs discovery/indexing on every call (evicting any cached session) so newly
    /// added or removed files are always picked up.
    /// </summary>
    public async Task<RepositoryContext> AnalyzeRepositoryAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var session = await RefreshSessionAsync(repositoryPath).ConfigureAwait(false);
        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} files={FileCount}",
            "repository_analyze", session.RootPath, stopwatch.ElapsedMilliseconds, session.Index.Files.Count);
        return session.Discovery.Context;
    }

    /// <summary>Engine/index status for a repository (§17).</summary>
    public async Task<AceStatus> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var session = await GetSessionAsync(repositoryPath).ConfigureAwait(false);

        var status = new AceStatus
        {
            RepositoryPath = session.RootPath,
            Indexed = true,
            FileCount = session.Index.Files.Count,
            SourceFileCount = session.Discovery.Context.SourceFileCount,
            NodeCount = session.Graph.GetNodes().Count,
            EdgeCount = session.Graph.GetEdges().Count,
            IndexVersion = session.Index.IndexVersion,
            AnalyzerVersion = session.Index.AnalyzerVersion,
            CurrentAnalyzerVersion = RepositoryIndex.CurrentAnalyzerVersion,
            Stale = session.WasStale,
            FailedFiles = session.FailedFiles,
            Languages = session.Discovery.Context.Languages,
            TestProjects = session.Discovery.Context.TestProjects,
            LastLoadedUtc = session.LoadedAtUtc,
        };

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} nodes={NodeCount} edges={EdgeCount}",
            "status", session.RootPath, stopwatch.ElapsedMilliseconds, status.NodeCount, status.EdgeCount);
        return status;
    }

    // ------------------------------------------------------------- graph

    /// <summary>
    /// Forces a graph rebuild. Re-scans the repository first (evicting any cached
    /// session) so the rebuild reflects the current on-disk state, then persists the graph.
    /// </summary>
    public async Task<GraphBuildInfo> BuildGraphAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var session = await RefreshSessionAsync(repositoryPath).ConfigureAwait(false);

        var graph = BuildAndPersistGraph(session.RootPath, session.Options, session.Discovery, session);
        session.Graph = graph;

        var info = new GraphBuildInfo
        {
            NodeCount = graph.GetNodes().Count,
            EdgeCount = graph.GetEdges().Count,
            DurationMs = stopwatch.ElapsedMilliseconds,
            PersistedPath = JsonGraphStore.GetGraphPath(session.RootPath, session.Options.IndexPath),
        };

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} nodes={NodeCount} edges={EdgeCount}",
            "graph_build", session.RootPath, info.DurationMs, info.NodeCount, info.EdgeCount);
        return info;
    }

    /// <summary>
    /// Neighbor query over the graph for a node id. When <paramref name="nodeId"/> is not
    /// an exact node id, falls back to name resolution via code search (same behavior as
    /// the CLI's graph query).
    /// </summary>
    public async Task<IReadOnlyList<GraphNode>> QueryGraphAsync(
        string repositoryPath,
        string nodeId,
        IReadOnlyCollection<EdgeType>? edgeTypes = null,
        EdgeDirection direction = EdgeDirection.Both,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var session = await GetSessionAsync(repositoryPath).ConfigureAwait(false);
        var graph = session.Graph;

        IReadOnlyList<GraphNode> neighbors;
        if (graph.TryGetNode(nodeId, out _))
        {
            neighbors = graph.GetNeighbors(nodeId, edgeTypes, direction);
        }
        else
        {
            // Not a node id: try to resolve it as a symbol name before returning empty.
            var matches = _searchService.Search(graph, nodeId);
            var target = matches.FirstOrDefault(m => string.Equals(m.Name, nodeId, StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault();
            neighbors = target is null ? [] : graph.GetNeighbors(target.Id, edgeTypes, direction);
        }

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} node={NodeId} results={ResultCount}",
            "graph_query", session.RootPath, stopwatch.ElapsedMilliseconds, nodeId, neighbors.Count);
        return neighbors;
    }

    /// <summary>Outgoing dependencies of a symbol (matched by name).</summary>
    public async Task<IReadOnlyList<GraphNode>> GetDependenciesAsync(
        string repositoryPath,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        var stopwatch = Stopwatch.StartNew();
        var session = await GetSessionAsync(repositoryPath).ConfigureAwait(false);

        var matches = session.Graph.FindNodesByName(symbol.Trim());
        var target = matches.FirstOrDefault(node => string.Equals(node.Name, symbol.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();

        var dependencies = target is null
            ? []
            : session.Graph.GetNeighbors(target.Id, DependencyEdgeTypes, EdgeDirection.Outgoing);

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} symbol={Symbol} results={ResultCount}",
            "dependencies_get", session.RootPath, stopwatch.ElapsedMilliseconds, symbol, dependencies.Count);
        return dependencies;
    }

    // ------------------------------------------------------------- intelligence

    /// <summary>Change impact analysis with risk merged in (FR-006/FR-008).</summary>
    public async Task<ImpactReport> AnalyzeImpactAsync(
        string repositoryPath,
        IEnumerable<string> changedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        var stopwatch = Stopwatch.StartNew();
        var session = await GetSessionAsync(repositoryPath).ConfigureAwait(false);

        // Capture the graph once: a concurrent rebuild must not change mid-analysis inputs.
        var graph = session.Graph;
        var analysis = _impactEngine.AnalyzeDetailed(graph, session.RootPath, changedFiles);
        var facts = RiskFacts.From(graph, analysis);
        var risk = _riskEngine.Analyze(analysis.Report, facts);

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} files={ChangedFiles} affected={AffectedCount} riskLevel={RiskLevel}",
            "impact_analyze", session.RootPath, stopwatch.ElapsedMilliseconds,
            analysis.ChangedFiles.Count, analysis.Report.AffectedComponents.Count, risk.RiskLevel);

        return analysis.Report with { RiskLevel = risk.RiskLevel, RiskScore = risk.RiskScore };
    }

    /// <summary>Deterministic risk scoring for a change set (FR-008).</summary>
    public async Task<RiskReport> AnalyzeRiskAsync(
        string repositoryPath,
        IEnumerable<string> changedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        var stopwatch = Stopwatch.StartNew();
        var session = await GetSessionAsync(repositoryPath).ConfigureAwait(false);

        var analysis = _impactEngine.AnalyzeDetailed(session.Graph, session.RootPath, changedFiles);
        var facts = RiskFacts.From(session.Graph, analysis);
        var risk = _riskEngine.Analyze(analysis.Report, facts);

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} riskLevel={RiskLevel} riskScore={RiskScore}",
            "risk_analyze", session.RootPath, stopwatch.ElapsedMilliseconds, risk.RiskLevel, risk.RiskScore);
        return risk;
    }

    /// <summary>Tests affected by a change set (FR-009).</summary>
    public async Task<TestImpactReport> GetAffectedTestsAsync(
        string repositoryPath,
        IEnumerable<string> changedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        var stopwatch = Stopwatch.StartNew();
        var session = await GetSessionAsync(repositoryPath).ConfigureAwait(false);

        var report = _testImpactEngine.Analyze(session.Graph, session.RootPath, changedFiles);

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} tests={TestCount}",
            "tests_affected", session.RootPath, stopwatch.ElapsedMilliseconds, report.AffectedTests.Count);
        return report;
    }

    /// <summary>Recommended regression scope (FR-010).</summary>
    public async Task<RegressionScope> GetRegressionScopeAsync(
        string repositoryPath,
        IEnumerable<string> changedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedFiles);
        var stopwatch = Stopwatch.StartNew();
        var session = await GetSessionAsync(repositoryPath).ConfigureAwait(false);

        // Capture the graph once: a concurrent rebuild must not change mid-analysis inputs.
        var graph = session.Graph;
        var scope = _regressionEngine.Analyze(graph, session.RootPath, changedFiles);

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} riskLevel={RiskLevel} scope={Scope}",
            "regression_scope", session.RootPath, stopwatch.ElapsedMilliseconds, scope.RiskLevel, scope.RecommendedScope);
        return scope;
    }

    /// <summary>Architecture violations per configured rules (FR-011).</summary>
    public async Task<IReadOnlyList<ArchitectureViolation>> AnalyzeArchitectureAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var session = await GetSessionAsync(repositoryPath).ConfigureAwait(false);

        var violations = _architectureEngine.Analyze(session.Graph, session.Options);

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} violations={ViolationCount}",
            "architecture_analyze", session.RootPath, stopwatch.ElapsedMilliseconds, violations.Count);
        return violations;
    }

    /// <summary>Prioritized context for a symbol/file query (FR-012).</summary>
    public async Task<IReadOnlyList<ContextItem>> GetContextAsync(
        string repositoryPath,
        string query,
        int maxItems = ContextEngine.DefaultMaxItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var stopwatch = Stopwatch.StartNew();
        var session = await GetSessionAsync(repositoryPath).ConfigureAwait(false);

        var violations = _architectureEngine.Analyze(session.Graph, session.Options);
        var items = _contextEngine.GetContext(session.Graph, query, session.Index, violations, maxItems);

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} query={Query} items={ItemCount}",
            "context_get", session.RootPath, stopwatch.ElapsedMilliseconds, query, items.Count);
        return items;
    }

    /// <summary>Case-insensitive symbol search over the graph (FR code search).</summary>
    public async Task<IReadOnlyList<SymbolLocation>> SearchCodeAsync(
        string repositoryPath,
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var stopwatch = Stopwatch.StartNew();
        var session = await GetSessionAsync(repositoryPath).ConfigureAwait(false);

        var results = _searchService.Search(session.Graph, query);

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} query={Query} results={ResultCount}",
            "code_search", session.RootPath, stopwatch.ElapsedMilliseconds, query, results.Count);
        return results;
    }

    // ------------------------------------------------------------- change inputs

    /// <summary>
    /// Resolves the changed-file set for change-set intelligence (FR-007): explicit
    /// <paramref name="changedFiles"/> stay the primary input; git-based inputs
    /// (<paramref name="useGitWorkingTree"/> / <paramref name="gitDiffRange"/>) are
    /// optional add-ons and are only honored when
    /// <see cref="AceOptions.EnableGitAnalysis"/> is true. Returns the merged,
    /// de-duplicated list; never returns an empty list.
    /// </summary>
    /// <exception cref="InvalidOperationException">Git inputs were requested but git analysis is disabled, git is unavailable, the path is not a repository, or git produced no files.</exception>
    /// <exception cref="ArgumentException">No changed files were provided at all.</exception>
    public async Task<IReadOnlyList<string>> ResolveChangedFilesAsync(
        string repositoryPath,
        IReadOnlyList<string> changedFiles,
        bool useGitWorkingTree = false,
        string? gitDiffRange = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var options = AceOptionsFactory.Load(repositoryPath);
        var gitFiles = new List<string>();

        if (!string.IsNullOrWhiteSpace(gitDiffRange))
        {
            RequireGitAnalysisEnabled(options);
            var diff = await _gitService.GetDiffFilesAsync(repositoryPath, gitDiffRange!, cancellationToken).ConfigureAwait(false);
            if (!diff.Available)
            {
                throw new InvalidOperationException($"git is not available ({diff.Error}). Pass changed files explicitly.");
            }

            if (!diff.IsRepository)
            {
                throw new InvalidOperationException($"'{repositoryPath}' is not a git repository; pass changed files explicitly.");
            }

            if (diff.ChangedFiles.Count == 0)
            {
                throw new InvalidOperationException($"No changed files found for diff range '{gitDiffRange}'.");
            }

            gitFiles.AddRange(diff.ChangedFiles);
        }

        if (useGitWorkingTree)
        {
            RequireGitAnalysisEnabled(options);
            var status = await _gitService.GetStatusAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
            if (!status.Available)
            {
                throw new InvalidOperationException($"git is not available ({status.Error}). Pass changed files explicitly.");
            }

            if (!status.IsRepository)
            {
                throw new InvalidOperationException($"'{repositoryPath}' is not a git repository; pass changed files explicitly.");
            }

            if (status.ChangedFiles.Count == 0)
            {
                throw new InvalidOperationException("The git working tree is clean; no changed files to analyze.");
            }

            gitFiles.AddRange(status.ChangedFiles);
        }

        var merged = changedFiles
            .Concat(gitFiles)
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (merged.Count == 0)
        {
            throw new ArgumentException(
                "No changed files provided. Pass changed files explicitly or enable git-based change detection.",
                nameof(changedFiles));
        }

        return merged;
    }

    private static void RequireGitAnalysisEnabled(AceOptions options)
    {
        if (!options.EnableGitAnalysis)
        {
            throw new InvalidOperationException(
                "git-based change detection is disabled (ace.enableGitAnalysis is false). " +
                "Pass changed files explicitly or set enableGitAnalysis to true in ace.json.");
        }
    }

    // ------------------------------------------------------------- session plumbing

    /// <summary>
    /// Evicts any cached session for the repository and re-runs discovery/indexing from
    /// disk, so newly added files are picked up by the next operation.
    /// </summary>
    private Task<RepoSession> RefreshSessionAsync(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var root = _fileSystem.GetFullPath(repositoryPath);
        _sessions.TryRemove(root, out _);
        return GetSessionAsync(root);
    }

    private Task<RepoSession> GetSessionAsync(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        var root = _fileSystem.GetFullPath(repositoryPath);
        if (!_fileSystem.DirectoryExists(root))
        {
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        }

        var lazy = _sessions.GetOrAdd(root, key => new Lazy<Task<RepoSession>>(
            () =>
            {
                var task = Task.Run(() => LoadSession(key));
                // A failed load must not poison the cache; allow a retry on next call.
                task.ContinueWith(
                    failed => _sessions.TryRemove(key, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return task;
            },
            LazyThreadSafetyMode.ExecutionAndPublication));

        return lazy.Value;
    }

    private RepoSession LoadSession(string root)
    {
        var stopwatch = Stopwatch.StartNew();

        var options = AceOptionsFactory.Load(root);

        // Defense in depth (SR-005): even after AceOptions.Normalize, verify the resolved
        // index directory stays inside the repository root before deriving artifact paths.
        try
        {
            PathGuard.EnsureWithinRoot(root, options.IndexPath);
        }
        catch (PathSecurityException)
        {
            _logger.LogWarning("IndexPath '{IndexPath}' escapes the repository root; falling back to '.ace'", options.IndexPath);
            options.IndexPath = ".ace";
        }

        var discovery = new RepositoryDiscovery(_fileSystem, options, logger: null).Discover(root);

        var updater = new IndexUpdater(_fileSystem, options);
        var previous = RepositoryIndex.Load(_fileSystem, root, options.IndexPath);
        var updateResult = updater.Update(discovery, previous, persist: true);

        var persistedGraph = _graphStore.Load(root, options.IndexPath);

        var needsRebuild = previous is null
                           || persistedGraph is null
                           || !string.Equals(previous.AnalyzerVersion, RepositoryIndex.CurrentAnalyzerVersion, StringComparison.Ordinal)
                           || updateResult.Diff.ChangedCount > 0;

        // "Stale" means persisted artifacts existed but were out of date; a first-time
        // build is not staleness.
        var wasStale = previous is not null &&
                       (persistedGraph is null ||
                        !string.Equals(previous.AnalyzerVersion, RepositoryIndex.CurrentAnalyzerVersion, StringComparison.Ordinal) ||
                        updateResult.Diff.ChangedCount > 0);

        var session = new RepoSession
        {
            RootPath = root,
            Options = options,
            Discovery = discovery,
            Index = updateResult.Index,
            LastUpdate = updateResult,
            WasStale = wasStale,
            LoadedAtUtc = DateTime.UtcNow,
        };

        session.Graph = needsRebuild
            ? BuildAndPersistGraph(root, options, discovery, session)
            : persistedGraph!;

        _logger.LogInformation(
            "ACE {Operation} repository={Repository} durationMs={DurationMs} files={FileCount} nodes={NodeCount} edges={EdgeCount} stale={Stale}",
            "ensure_index", root, stopwatch.ElapsedMilliseconds,
            session.Index.Files.Count, session.Graph.GetNodes().Count, session.Graph.GetEdges().Count, wasStale);

        return session;
    }

    private ICodeGraph BuildAndPersistGraph(string root, AceOptions options, DiscoveryResult discovery, RepoSession session)
    {
        var analyzer = new CSharpAnalyzer();
        var sourceFiles = discovery.Files
            .Where(file => file.Category is FileCategory.Source or FileCategory.Test)
            .ToList();

        var analyzed = new ConcurrentBag<AnalyzedFile>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = AceOptions.ClampParallelism(options.MaxParallelism),
        };

        Parallel.ForEach(sourceFiles, parallelOptions, file =>
        {
            try
            {
                var content = _fileSystem.ReadAllText(file.FullPath);
                var analysis = analyzer.AnalyzeAsync(file.RelativePath, content).GetAwaiter().GetResult();
                analyzed.Add(new AnalyzedFile(file.RelativePath, analysis));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Per-file failure isolation (§17): record and continue.
                session.ParseFailures[file.RelativePath] = ex.Message;
                _logger.LogWarning("Failed to parse {File}: {Error}", file.RelativePath, ex.Message);
            }
        });

        var projects = new List<CsprojInfo>();
        foreach (var projectFile in discovery.Files.Where(file => file.Category == FileCategory.Project))
        {
            try
            {
                var content = _fileSystem.ReadAllText(projectFile.FullPath);
                if (CsprojInfo.TryParse(projectFile.RelativePath, content) is { } info)
                {
                    projects.Add(info);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                session.ParseFailures[projectFile.RelativePath] = ex.Message;
            }
        }

        var graph = new GraphBuilder().Build(
            analyzed.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToList(),
            projects);

        _graphStore.Save(graph, root, options.IndexPath);
        return graph;
    }

    /// <summary>Cached per-repository state shared by all facade operations.</summary>
    private sealed class RepoSession
    {
        public required string RootPath { get; init; }

        public required AceOptions Options { get; init; }

        public required DiscoveryResult Discovery { get; init; }

        public required RepositoryIndex Index { get; init; }

        public required IndexUpdateResult LastUpdate { get; init; }

        public ICodeGraph Graph { get; set; } = null!;

        public bool WasStale { get; init; }

        public DateTime LoadedAtUtc { get; init; }

        public ConcurrentDictionary<string, string> ParseFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> FailedFiles =>
            LastUpdate.FailedFiles
                .Concat(ParseFailures)
                .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
    }
}
