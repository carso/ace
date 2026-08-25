namespace Ace.Core.Platform;

/// <summary>
/// Result of <see cref="IGitService.GetStatusAsync"/>. Never produced by throwing:
/// when git is missing or the path is not a repository, <see cref="Available"/> /
/// <see cref="IsRepository"/> are false and callers fall back to explicit file lists (FR-007).
/// </summary>
public sealed record GitStatusResult(
    bool Available,
    bool IsRepository,
    IReadOnlyList<string> ChangedFiles,
    string? Error = null);

/// <summary>Result of <see cref="IGitService.GetDiffFilesAsync"/> for a revision range.</summary>
public sealed record GitDiffResult(
    bool Available,
    bool IsRepository,
    string Range,
    IReadOnlyList<string> ChangedFiles,
    string? Error = null);

/// <summary>
/// Git abstraction (SRS §13). Implemented by shelling out to the git CLI; never throws —
/// failures surface as structured "unavailable / no-repo" results.
/// </summary>
public interface IGitService
{
    /// <summary>Changed files per <c>git status --porcelain</c> (repository-relative paths).</summary>
    Task<GitStatusResult> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default);

    /// <summary>Changed files per <c>git diff --name-only &lt;range&gt;</c> (repository-relative paths).</summary>
    Task<GitDiffResult> GetDiffFilesAsync(string repositoryPath, string range, CancellationToken cancellationToken = default);
}
