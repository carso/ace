namespace Ace.Core.Parsing;

/// <summary>
/// Pluggable language analyzer seam (SRS §22). Implementations must be syntax-only,
/// resilient to malformed input (never throw; report diagnostics) and safe for
/// concurrent use.
/// </summary>
public interface IAnalyzer
{
    /// <summary>Language this analyzer handles, e.g. "C#".</summary>
    string Language { get; }

    /// <summary>Version of the analyzer, recorded in index entries and graph edges.</summary>
    string Version { get; }

    /// <summary>True when this analyzer can handle the file at <paramref name="path"/> (by extension).</summary>
    bool CanHandle(string path);

    /// <summary>
    /// Analyzes the content of a single source file. Must never throw for malformed
    /// input: parse problems are returned as diagnostics on a (possibly partial)
    /// <see cref="FileAnalysis"/> (SRS §17).
    /// </summary>
    /// <param name="path">Repository-relative path of the file (forward slashes).</param>
    /// <param name="content">Full text content of the file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<FileAnalysis> AnalyzeAsync(string path, string content, CancellationToken cancellationToken = default);
}
