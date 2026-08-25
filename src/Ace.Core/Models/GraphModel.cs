namespace Ace.Core.Models;

/// <summary>Node types in the ACE graph (SRS §10).</summary>
public enum NodeType
{
    Repository,
    Solution,
    Project,
    Package,
    Namespace,
    Class,
    Interface,
    Record,
    Method,
    Property,
    Field,
    Api,
    Database,
    Table,
    StoredProcedure,
    Test,
    Configuration,
}

/// <summary>Edge types in the ACE graph (SRS §10).</summary>
public enum EdgeType
{
    Contains,
    References,
    Calls,
    Implements,
    Inherits,
    DependsOn,
    Uses,
    Tests,
    Exposes,
    Configures,
    Reads,
    Writes,
}

/// <summary>
/// How strongly an edge is grounded in evidence (SRS §4.3, §10).
/// Observed: read directly from source/markup. Calculated: derived deterministically
/// from observed facts. Inferred: heuristic (name matching etc.), always &lt; 1.0 confidence.
/// </summary>
public enum Confidence
{
    Observed,
    Calculated,
    Inferred,
}

/// <summary>A node in the ACE code graph. IDs are stable strings, e.g. "Project:Namespace.Type#Member".</summary>
public sealed record GraphNode
{
    /// <summary>Stable, unique identifier within the repository graph.</summary>
    public required string Id { get; init; }

    public NodeType Type { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>Repository-relative file path this node was observed in, if any.</summary>
    public string? FilePath { get; init; }

    /// <summary>Owning project name, if any.</summary>
    public string? Project { get; init; }

    /// <summary>Declaring namespace, if any.</summary>
    public string? Namespace { get; init; }

    /// <summary>Free-form metadata (attributes, signatures, diagnostics...).</summary>
    public Dictionary<string, object?> Metadata { get; init; } = new();
}

/// <summary>A directed relationship between two graph nodes (SRS §10).</summary>
public sealed record GraphEdge
{
    public required string SourceId { get; init; }

    public required string TargetId { get; init; }

    public EdgeType Type { get; init; }

    public Confidence Confidence { get; init; } = Confidence.Observed;

    /// <summary>0.0–1.0. Heuristic edges must carry a score below 1.0.</summary>
    public double ConfidenceScore { get; init; } = 1.0;

    /// <summary>Why this edge exists (e.g. "name-match", "base-list", "invocation").</summary>
    public string? Evidence { get; init; }

    /// <summary>Where the fact was observed, typically "path/file.cs:line".</summary>
    public string? Location { get; init; }

    /// <summary>Analyzer that produced the edge, e.g. "csharp-roslyn/1.0".</summary>
    public string? Analyzer { get; init; }

    public DateTime? CreatedAt { get; init; }
}

/// <summary>Structured repository context (FR-002).</summary>
public sealed record RepositoryContext
{
    public string RepositoryPath { get; init; } = string.Empty;

    public int FileCount { get; init; }

    public int SourceFileCount { get; init; }

    public int ProjectCount { get; init; }

    public IReadOnlyList<string> Languages { get; init; } = [];

    public IReadOnlyList<string> Frameworks { get; init; } = [];

    public IReadOnlyList<string> BuildSystems { get; init; } = [];

    public IReadOnlyList<string> TestProjects { get; init; } = [];

    public IReadOnlyList<string> DependencySystems { get; init; } = [];
}

/// <summary>A single indexed file entry (SRS §11).</summary>
public sealed record IndexEntry
{
    /// <summary>Repository-relative path using forward slashes.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Content hash (SHA-256, hex). May be empty until computed.</summary>
    public string Hash { get; init; } = string.Empty;

    public long Size { get; init; }

    public DateTime LastModifiedUtc { get; init; }

    /// <summary>File classification: source / project / solution / config / test / manifest / doc / other.</summary>
    public string? Category { get; init; }
}
