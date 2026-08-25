using System.Collections.Concurrent;
using System.Security.Cryptography;
using Ace.Core.Configuration;
using Ace.Core.Discovery;
using Ace.Core.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ace.Core.Indexing;

/// <summary>Incremental diff between the previous persisted index and the current file set.</summary>
public sealed record IndexDiff
{
    public required IReadOnlyList<string> Added { get; init; }

    public required IReadOnlyList<string> Modified { get; init; }

    public required IReadOnlyList<string> Deleted { get; init; }

    public int ChangedCount => Added.Count + Modified.Count + Deleted.Count;
}

/// <summary>Result of an index update run.</summary>
public sealed record IndexUpdateResult
{
    public required RepositoryIndex Index { get; init; }

    public required IndexDiff Diff { get; init; }

    /// <summary>Files that could not be hashed/read; they were recorded and skipped, never aborting the run (SRS §17).</summary>
    public required IReadOnlyDictionary<string, string> FailedFiles { get; init; }

    /// <summary>Files skipped because size+timestamp matched the previous entry and no hash was recomputed.</summary>
    public int UnchangedCount { get; init; }
}

/// <summary>
/// Builds and incrementally updates the repository index (SRS §11, §17).
/// Fast path: compare size + last-write timestamp; hash (SHA-256) only files that are
/// new or size/timestamp-changed. Hashing runs with bounded parallelism. Writes are
/// atomic-ish (temp file + rename) so a failed update preserves the previous valid index.
/// </summary>
public sealed class IndexUpdater
{
    private readonly IFileSystemService _fileSystem;
    private readonly AceOptions _options;
    private readonly ILogger<IndexUpdater> _logger;

    public IndexUpdater(IFileSystemService fileSystem, AceOptions options, ILogger<IndexUpdater>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<IndexUpdater>.Instance;
    }

    /// <summary>
    /// Updates (or creates) the index for the discovered files of a repository and
    /// persists it to <c>&lt;root&gt;/&lt;indexPath&gt;/index.json</c>.
    /// </summary>
    public IndexUpdateResult Update(DiscoveryResult discovery, RepositoryIndex? previous = null, bool persist = true)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        previous ??= RepositoryIndex.Load(_fileSystem, discovery.RootPath, _options.IndexPath);
        previous ??= new RepositoryIndex { Repository = discovery.RootPath };

        var currentPaths = new HashSet<string>(discovery.Files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
        var categoryByPath = discovery.Files.ToDictionary(f => f.RelativePath, f => f.Category, StringComparer.OrdinalIgnoreCase);

        // --- Classify: unchanged (fast path) vs needs hashing ---
        var toHash = new List<DiscoveredFile>();
        var reused = new Dictionary<string, StoredIndexEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in discovery.Files)
        {
            if (previous.Files.TryGetValue(file.RelativePath, out var old))
            {
                long size;
                DateTime lastWriteUtc;
                try
                {
                    size = _fileSystem.GetFileSize(file.FullPath);
                    lastWriteUtc = _fileSystem.GetLastWriteTimeUtc(file.FullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Cannot stat {File}: {Error}", file.RelativePath, ex.Message);
                    toHash.Add(file);
                    continue;
                }

                // Fast path: unchanged size + timestamp → reuse previous hash, no I/O on content.
                if (size == old.Size && lastWriteUtc == old.LastWriteUtc && !string.IsNullOrEmpty(old.Hash))
                {
                    reused[file.RelativePath] = old;
                    continue;
                }
            }

            toHash.Add(file);
        }

        // --- Hash new/touched files with bounded parallelism, per-file failure isolation ---
        var hashed = new ConcurrentDictionary<string, StoredIndexEntry>(StringComparer.OrdinalIgnoreCase);
        var failures = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = AceOptions.ClampParallelism(_options.MaxParallelism),
        };

        Parallel.For(0, toHash.Count, parallelOptions, i =>
        {
            var file = toHash[i];
            try
            {
                var entry = CreateEntry(file, categoryByPath.GetValueOrDefault(file.RelativePath));
                hashed[file.RelativePath] = entry;
            }
            catch (Exception ex)
            {
                // A failing file is recorded and skipped; it never aborts indexing (SRS §17).
                failures[file.RelativePath] = ex.Message;
                _logger.LogWarning("Failed to hash {File}: {Error}", file.RelativePath, ex.Message);
            }
        });

        // --- Assemble new index ---
        var newIndex = new RepositoryIndex
        {
            Repository = discovery.RootPath,
            IndexVersion = previous.IndexVersion + (previous.Files.Count > 0 ? 1 : 0),
            AnalyzerVersion = RepositoryIndex.CurrentAnalyzerVersion,
        };

        foreach (var (path, entry) in reused)
        {
            newIndex.Files[path] = entry;
        }

        foreach (var (path, entry) in hashed)
        {
            newIndex.Files[path] = entry;
        }

        // --- Diff vs previous ---
        var added = new List<string>();
        var modified = new List<string>();
        foreach (var path in currentPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!newIndex.Files.TryGetValue(path, out var entry))
            {
                continue; // failed file
            }

            if (!previous.Files.TryGetValue(path, out var old))
            {
                added.Add(path);
            }
            else if (reused.ContainsKey(path))
            {
                // Fast path: unchanged by size+timestamp.
            }
            else if (!string.Equals(entry.Hash, old.Hash, StringComparison.OrdinalIgnoreCase))
            {
                // Timestamp/size changed but content identical → not a real modification.
                modified.Add(path);
            }
        }

        var deleted = previous.Files.Keys
            .Where(p => !currentPaths.Contains(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new IndexUpdateResult
        {
            Index = newIndex,
            Diff = new IndexDiff { Added = added, Modified = modified, Deleted = deleted },
            FailedFiles = failures,
            UnchangedCount = reused.Count,
        };

        if (persist)
        {
            newIndex.Save(_fileSystem, discovery.RootPath, _options.IndexPath);
        }

        return result;
    }

    private StoredIndexEntry CreateEntry(DiscoveredFile file, string? category)
    {
        // Stat BEFORE hashing, hash, then re-stat: if the file changed while it was
        // being hashed, refuse to pin a stale hash — the per-file failure handler
        // records it and the next run retries (SRS §17).
        var sizeBefore = _fileSystem.GetFileSize(file.FullPath);
        var lastWriteBefore = _fileSystem.GetLastWriteTimeUtc(file.FullPath);

        var hash = ComputeSha256(file.FullPath);

        var sizeAfter = _fileSystem.GetFileSize(file.FullPath);
        var lastWriteAfter = _fileSystem.GetLastWriteTimeUtc(file.FullPath);
        if (sizeBefore != sizeAfter || lastWriteBefore != lastWriteAfter)
        {
            throw new IOException($"File changed while it was being hashed: {file.RelativePath}");
        }

        return new StoredIndexEntry
        {
            Hash = hash,
            Size = sizeAfter,
            LastWriteUtc = lastWriteAfter,
            Category = category,
            AnalyzerVersion = RepositoryIndex.CurrentAnalyzerVersion,
        };
    }

    private string ComputeSha256(string fullPath)
    {
        using var stream = _fileSystem.OpenRead(fullPath);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
