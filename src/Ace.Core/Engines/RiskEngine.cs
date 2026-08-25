using Ace.Core.Graph;
using Ace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ace.Core.Engines;

/// <summary>
/// Graph-derived facts the risk model scores against. Constructed from a full
/// <see cref="ImpactAnalysis"/> via <see cref="From"/>, or directly for synthetic scenarios.
/// </summary>
public sealed record RiskFacts
{
    /// <summary>A changed or directly affected component is a public API surface.</summary>
    public bool PublicApiExposed { get; init; }

    /// <summary>The impact reaches projects beyond the ones containing the changes.</summary>
    public bool CrossProjectImpact { get; init; }

    /// <summary>Configuration or database files participate in the change set.</summary>
    public bool ConfigOrDatabaseChanged { get; init; }

    private static readonly HashSet<string> ConfigOrDatabaseExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".xml", ".yaml", ".yml", ".config", ".ini", ".toml", ".sql", ".env", ".props",
    };

    /// <summary>Derives risk facts from graph + impact closure.</summary>
    public static RiskFacts From(ICodeGraph graph, ImpactAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(analysis);

        // Public-API exposure: changed nodes or depth-1 affected nodes that are public
        // members/types inside an Api project or controllers. TryGetNode keeps a
        // concurrent graph rebuild from throwing mid-derivation.
        var exposureIds = analysis.ChangedNodeIds
            .Concat(analysis.Closure.Where(kv => kv.Value == 1).Select(kv => kv.Key));

        var publicApiExposed = exposureIds.Any(id =>
            graph.TryGetNode(id, out var node) &&
            node is not null &&
            GraphMeta.GetBool(node, "isPublic") &&
            (node.Name.Contains("Controller", StringComparison.OrdinalIgnoreCase) ||
             node.Project?.Contains("Api", StringComparison.OrdinalIgnoreCase) == true));

        // Cross-project impact: affected projects strictly beyond the changed projects.
        var changedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in analysis.ChangedNodeIds)
        {
            if (graph.TryGetNode(id, out var node) && node is { Project: { Length: > 0 } })
            {
                changedProjects.Add(node.Project);
            }
        }

        var crossProject = analysis.Report.AffectedProjects.Any(project => !changedProjects.Contains(project));

        var configOrDatabase = analysis.ChangedFiles.Any(file =>
            ConfigOrDatabaseExtensions.Contains(Path.GetExtension(file)));

        return new RiskFacts
        {
            PublicApiExposed = publicApiExposed,
            CrossProjectImpact = crossProject,
            ConfigOrDatabaseChanged = configOrDatabase,
        };
    }
}

/// <summary>
/// Deterministic weighted risk scoring (FR-008). Pure function of an
/// <see cref="ImpactReport"/> plus <see cref="RiskFacts"/>: no randomness, no I/O.
/// Factor weights live in <see cref="RiskRules"/>.
/// </summary>
public sealed class RiskEngine
{
    private readonly ILogger<RiskEngine> _logger;

    public RiskEngine(ILogger<RiskEngine>? logger = null)
        => _logger = logger ?? NullLogger<RiskEngine>.Instance;

    /// <summary>Scores a change set and returns the weighted risk report (0–100 + band).</summary>
    public RiskReport Analyze(ImpactReport impact, RiskFacts facts)
    {
        ArgumentNullException.ThrowIfNull(impact);
        ArgumentNullException.ThrowIfNull(facts);

        var affectedCount = impact.AffectedComponents.Count;
        var countValue = Math.Min(1.0, affectedCount / (double)RiskRules.AffectedCountSaturation);
        var depthValue = Math.Min(1.0, impact.MaxDepthReached / (double)RiskRules.MaxDepthForScore);
        var publicApiValue = facts.PublicApiExposed ? 1.0 : 0.0;
        var crossProjectValue = facts.CrossProjectImpact ? 1.0 : 0.0;

        var coverageValue = impact.AffectedTests.Count switch
        {
            0 when affectedCount > 0 => 1.0,
            0 => 0.0,
            var tests when tests < affectedCount * RiskRules.PartialCoverageRatio => 0.5,
            _ => 0.0,
        };

        var configValue = facts.ConfigOrDatabaseChanged ? 1.0 : 0.0;

        var factors = new List<RiskFactor>
        {
            new()
            {
                Name = "affected-components",
                Weight = RiskRules.WeightAffectedComponents * countValue,
                Detail = $"{affectedCount} affected component(s)",
            },
            new()
            {
                Name = "dependency-depth",
                Weight = RiskRules.WeightDependencyDepth * depthValue,
                Detail = $"impact depth {impact.MaxDepthReached} of {RiskRules.MaxDepthForScore}",
            },
            new()
            {
                Name = "public-api-exposure",
                Weight = RiskRules.WeightPublicApiExposure * publicApiValue,
                Detail = facts.PublicApiExposed ? "public API surface affected" : "no public API exposure",
            },
            new()
            {
                Name = "cross-project-impact",
                Weight = RiskRules.WeightCrossProjectImpact * crossProjectValue,
                Detail = facts.CrossProjectImpact ? "impact crosses project boundaries" : "impact contained in one project",
            },
            new()
            {
                Name = "test-coverage-gap",
                Weight = RiskRules.WeightTestCoverageGap * coverageValue,
                Detail = impact.AffectedTests.Count == 0
                    ? "no affected tests identified"
                    : $"{impact.AffectedTests.Count} affected test(s) for {affectedCount} component(s)",
            },
            new()
            {
                Name = "config-or-database-change",
                Weight = RiskRules.WeightConfigOrDatabaseChange * configValue,
                Detail = facts.ConfigOrDatabaseChanged ? "configuration/database file changed" : "no config/db changes",
            },
        };

        var rawScore = factors.Sum(factor => factor.Weight);
        var score = RiskRules.ClampScore(rawScore);
        var level = RiskRules.Band(score);

        _logger.LogDebug("Risk score {Score} ({Level}) from {Factors} factors", score, level, factors.Count);

        return new RiskReport
        {
            RiskLevel = level,
            RiskScore = score,
            Factors = factors,
        };
    }
}
