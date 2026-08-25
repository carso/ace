namespace Ace.Core.Configuration;

/// <summary>
/// One allowed-layer-direction rule for architecture analysis (FR-011).
/// Layers are ordered outer-to-inner (e.g. Controller, Service, Repository);
/// a component in layer i may depend only on layers with a higher index.
/// </summary>
public sealed record ArchitectureRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Layers ordered from outermost to innermost.</summary>
    public List<string> Layers { get; set; } = [];
}

/// <summary>
/// ACE configuration (SRS §19). Loaded by <see cref="AceOptionsFactory"/> from
/// defaults → optional <c>ace.json</c> in the repository root → <c>ACE__*</c>
/// environment variables (later sources win).
/// </summary>
public sealed class AceOptions
{
    /// <summary>Configuration section name used by ace.json and ACE__* environment variables.</summary>
    public const string SectionName = "ace";

    /// <summary>Directory (relative to the repository root) where ACE stores index and graph data.</summary>
    public string IndexPath { get; set; } = ".ace";

    /// <summary>Maximum degree of parallelism for enumeration/hashing/parsing. Clamped to 1..64.</summary>
    public int MaxParallelism { get; set; } = ClampParallelism(Environment.ProcessorCount);

    /// <summary>Whether ACE may shell out to git for change detection (FR-007).</summary>
    public bool EnableGitAnalysis { get; set; }

    /// <summary>Whether architecture-rule analysis is enabled (FR-011). Defaults to true (SRS §19).</summary>
    public bool EnableArchitectureAnalysis { get; set; } = true;

    /// <summary>Directory names excluded from discovery/indexing. Compared case-insensitively.</summary>
    public List<string> ExclusionPatterns { get; set; } =
    [
        ".git",
        "bin",
        "obj",
        "node_modules",
        "packages",
        ".vscode",
        ".idea",
    ];

    /// <summary>
    /// Glob-style patterns for files ACE must never index or expose (SR-006).
    /// Compared case-insensitively.
    /// </summary>
    public List<string> SensitiveFilePatterns { get; set; } =
    [
        ".env",
        "*.key",
        "*.pem",
        "secrets.json",
        "credentials.json",
    ];

    /// <summary>Allowed-layer-direction rules for architecture analysis.</summary>
    public List<ArchitectureRule> ArchitectureRules { get; set; } = [];

    /// <summary>Lower bound for <see cref="MaxParallelism"/>.</summary>
    public const int MinParallelism = 1;

    /// <summary>Upper bound for <see cref="MaxParallelism"/>.</summary>
    public const int MaxAllowedParallelism = 64;

    /// <summary>Clamps a parallelism value into the supported 1..64 range.</summary>
    public static int ClampParallelism(int value) => Math.Clamp(value, MinParallelism, MaxAllowedParallelism);

    /// <summary>Re-applies invariants after external binding (config files, environment).</summary>
    public void Normalize()
    {
        MaxParallelism = ClampParallelism(MaxParallelism);

        // Containment (SR-005): the index directory must be a plain root-relative name —
        // rooted paths or '.'/'..' segments would move ACE artifacts outside the repository.
        if (string.IsNullOrWhiteSpace(IndexPath) ||
            Path.IsPathRooted(IndexPath) ||
            ContainsTraversalSegment(IndexPath))
        {
            IndexPath = ".ace";
        }

        ExclusionPatterns ??= [];
        SensitiveFilePatterns ??= [];
        ArchitectureRules ??= [];
    }

    /// <summary>True when any path segment is '.' or '..'.</summary>
    private static bool ContainsTraversalSegment(string path)
        => path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or "..");
}
