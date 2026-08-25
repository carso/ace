using System.Reflection;
using System.Text.Json;
using Ace.Core.Models;
using Ace.Core.Platform;

namespace Ace.Core.Indexing;

/// <summary>A file entry as persisted in the repository index (SRS §11).</summary>
public sealed record StoredIndexEntry
{
    /// <summary>SHA-256 content hash, lowercase hex.</summary>
    public string Hash { get; init; } = string.Empty;

    public long Size { get; init; }

    public DateTime LastWriteUtc { get; init; }

    /// <summary>Discovery category (source/project/solution/config/test/manifest/doc/other).</summary>
    public string? Category { get; init; }

    /// <summary>Version of the analyzer/indexer that processed this file.</summary>
    public string AnalyzerVersion { get; init; } = string.Empty;
}

/// <summary>
/// The persisted repository index (SRS §11): repository path, index version,
/// analyzer version and one entry per indexed file keyed by repository-relative path.
/// </summary>
public sealed class RepositoryIndex
{
    /// <summary>Version of the ACE analyzer/indexer recorded in the index.</summary>
    public static readonly string CurrentAnalyzerVersion =
        typeof(RepositoryIndex).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    public string Repository { get; set; } = string.Empty;

    public int IndexVersion { get; set; } = 1;

    public string AnalyzerVersion { get; set; } = CurrentAnalyzerVersion;

    public Dictionary<string, StoredIndexEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Path of the index file for a repository root: <c>&lt;root&gt;/.ace/index.json</c> (SRS §11).</summary>
    public static string GetIndexPath(string repositoryRoot, string indexDirectory = ".ace")
        => Path.Combine(repositoryRoot, indexDirectory, "index.json");

    /// <summary>Loads the persisted index for a repository, or null when absent/corrupt (SRS §17 recovery).</summary>
    public static RepositoryIndex? Load(IFileSystemService fileSystem, string repositoryRoot, string indexDirectory = ".ace")
    {
        var path = GetIndexPath(repositoryRoot, indexDirectory);
        if (!fileSystem.FileExists(path))
        {
            return null;
        }

        try
        {
            var json = fileSystem.ReadAllText(path);
            var index = JsonSerializer.Deserialize<RepositoryIndex>(json, AceJson.Options);
            if (index is null || string.IsNullOrEmpty(index.Repository))
            {
                return null;
            }

            // An index stamped for a different repository root (e.g. copied along with
            // the sources) is treated as absent → full re-index (SRS §17 recovery).
            if (!IsSamePath(index.Repository, repositoryRoot))
            {
                return null;
            }

            index.Files ??= new Dictionary<string, StoredIndexEntry>(StringComparer.OrdinalIgnoreCase);
            return index;
        }
        catch (JsonException)
        {
            // Corrupt index: treat as absent; the previous valid state is unrecoverable
            // but a full re-index is safe (SRS §17).
            return null;
        }
    }

    /// <summary>Case-insensitive path equality on Windows, ordinal elsewhere.</summary>
    internal static bool IsSamePath(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            comparison);
    }

    /// <summary>
    /// Persists the index via temp file + rename so a failed write preserves the
    /// previous valid index (SRS §17).
    /// </summary>
    public void Save(IFileSystemService fileSystem, string repositoryRoot, string indexDirectory = ".ace")
    {
        var destination = GetIndexPath(repositoryRoot, indexDirectory);
        var directory = Path.GetDirectoryName(destination)!;
        fileSystem.CreateDirectory(directory);

        var tempPath = destination + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var json = JsonSerializer.Serialize(this, AceJson.Options);
            fileSystem.WriteAllText(tempPath, json);
            fileSystem.ReplaceFile(tempPath, destination);
        }
        finally
        {
            // Never leave a stray temp file behind if the move failed.
            if (fileSystem.FileExists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort only; a leftover temp file is harmless.
                }
            }
        }
    }
}
