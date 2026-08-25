namespace Ace.Core.Models;

/// <summary>Discrete risk bands produced by the risk engine (FR-008).</summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
}

/// <summary>One hop of evidence: "source --relationship--> target" (SRS §9).</summary>
public sealed record EvidenceLink
{
    public string Source { get; init; } = string.Empty;

    public string Relationship { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;
}

/// <summary>Change impact analysis result (FR-006, SRS §9 response shape).</summary>
public sealed record ImpactReport
{
    public RiskLevel RiskLevel { get; init; }

    public int RiskScore { get; init; }

    public IReadOnlyList<string> ChangedComponents { get; init; } = [];

    /// <summary>All impacted components (direct + indirect), excluding the changed ones.</summary>
    public IReadOnlyList<string> AffectedComponents { get; init; } = [];

    /// <summary>Components reached in exactly one hop from a changed component.</summary>
    public IReadOnlyList<string> DirectAffectedComponents { get; init; } = [];

    /// <summary>Components reached in two or more hops.</summary>
    public IReadOnlyList<string> IndirectAffectedComponents { get; init; } = [];

    /// <summary>Owning projects of changed/affected components.</summary>
    public IReadOnlyList<string> AffectedProjects { get; init; } = [];

    /// <summary>Affected API surface (controllers/API nodes and members).</summary>
    public IReadOnlyList<string> AffectedApis { get; init; } = [];

    public IReadOnlyList<string> AffectedTests { get; init; } = [];

    /// <summary>Deepest hop distance reached by the traversal (0 when nothing is affected).</summary>
    public int MaxDepthReached { get; init; }

    public IReadOnlyList<EvidenceLink> Evidence { get; init; } = [];

    /// <summary>True when traversal hit the depth/visit caps and the closure was truncated.</summary>
    public bool Truncated { get; init; }
}

/// <summary>One weighted factor that contributed to a risk score.</summary>
public sealed record RiskFactor
{
    public string Name { get; init; } = string.Empty;

    public double Weight { get; init; }

    public string? Detail { get; init; }
}

/// <summary>Deterministic risk assessment (FR-008).</summary>
public sealed record RiskReport
{
    public RiskLevel RiskLevel { get; init; }

    /// <summary>0–100.</summary>
    public int RiskScore { get; init; }

    public IReadOnlyList<RiskFactor> Factors { get; init; } = [];
}

/// <summary>A test affected by a change set, with the reason it was selected.</summary>
public sealed record AffectedTest
{
    public string Name { get; init; } = string.Empty;

    public string? FilePath { get; init; }

    /// <summary>Why this test is affected, e.g. "tests changed component" / "in impact closure".</summary>
    public string? Reason { get; init; }
}

/// <summary>Tests affected by a change set (FR-009).</summary>
public sealed record TestImpactReport
{
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public IReadOnlyList<AffectedTest> AffectedTests { get; init; } = [];

    public IReadOnlyList<EvidenceLink> Evidence { get; init; } = [];
}

/// <summary>Recommended regression scope (FR-010).</summary>
public sealed record RegressionScope
{
    public RiskLevel RiskLevel { get; init; }

    /// <summary>Human-readable recommendation, e.g. "Run affected unit tests" / "Full regression".</summary>
    public string RecommendedScope { get; init; } = string.Empty;

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    /// <summary>Production components potentially impacted by the change set.</summary>
    public IReadOnlyList<string> PotentialImpact { get; init; } = [];

    public IReadOnlyList<AffectedTest> AffectedTests { get; init; } = [];

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>An architecture rule violation detected in the graph (FR-011).</summary>
public sealed record ArchitectureViolation
{
    /// <summary>Name of the rule that was violated.</summary>
    public string Rule { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;

    public EdgeType EdgeType { get; init; }

    /// <summary>"Violation" for clear inversions, "PotentialViolation" for single-step ones.</summary>
    public string Severity { get; init; } = "Violation";

    public string? Location { get; init; }

    public string? Message { get; init; }
}

/// <summary>One prioritized item of agent context (FR-012, 7-tier ranking).</summary>
public sealed record ContextItem
{
    /// <summary>Prioritization tier: 1 = direct code … 7 = repository context.</summary>
    public int Tier { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Repository-relative path, if the item maps to a file.</summary>
    public string? Path { get; init; }

    /// <summary>Why this item was included.</summary>
    public string? Reason { get; init; }

    /// <summary>Ranking score within the tier (higher = more relevant).</summary>
    public double Score { get; init; }
}
