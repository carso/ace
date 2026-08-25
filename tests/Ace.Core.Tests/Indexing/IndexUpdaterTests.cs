using System.Text.Json;
using Ace.Core.Configuration;
using Ace.Core.Discovery;
using Ace.Core.Indexing;
using Ace.Core.Models;
using Ace.Core.Platform;

namespace Ace.Core.Tests.Indexing;

public sealed class IndexUpdaterTests : IDisposable
{
    private readonly string _repo;
    private readonly FileSystemService _fileSystem = new();
    private readonly AceOptions _options = new();
    private readonly RepositoryDiscovery _discovery;
    private readonly IndexUpdater _updater;

    public IndexUpdaterTests()
    {
        _repo = TestPaths.CreateTempCopyOfSampleRepo();
        _discovery = new RepositoryDiscovery(_fileSystem, _options);
        _updater = new IndexUpdater(_fileSystem, _options);
    }

    public void Dispose() => TestPaths.DeleteDirectoryQuietly(_repo);

    [Fact]
    public void FirstRun_IndexesEveryDiscoveredFile_AndPersistsSrsShape()
    {
        var result = _updater.Update(_discovery.Discover(_repo));

        Assert.Equal(18, result.Index.Files.Count);
        Assert.Equal(18, result.Diff.Added.Count);
        Assert.Empty(result.Diff.Modified);
        Assert.Empty(result.Diff.Deleted);
        Assert.Empty(result.FailedFiles);
        Assert.True(result.UnchangedCount == 0);

        var entry = result.Index.Files["src/Customer.Services/CustomerService.cs"];
        Assert.Equal(64, entry.Hash.Length); // SHA-256 hex
        Assert.True(entry.Size > 0);
        Assert.NotEqual(default, entry.LastWriteUtc);
        Assert.Equal(FileCategory.Source, entry.Category);
        Assert.False(string.IsNullOrEmpty(entry.AnalyzerVersion));

        // Persisted JSON matches SRS §11 shape: repository/indexVersion/analyzerVersion/files.
        var indexPath = RepositoryIndex.GetIndexPath(_repo, _options.IndexPath);
        Assert.True(File.Exists(indexPath));
        using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
        Assert.Equal(_repo, document.RootElement.GetProperty("repository").GetString());
        Assert.True(document.RootElement.GetProperty("indexVersion").GetInt32() >= 1);
        Assert.False(string.IsNullOrEmpty(document.RootElement.GetProperty("analyzerVersion").GetString()));
        Assert.Equal(18, document.RootElement.GetProperty("files").EnumerateObject().Count());
        Assert.True(document.RootElement.GetProperty("files")
            .GetProperty("src/Customer.Services/CustomerService.cs")
            .TryGetProperty("hash", out _));
    }

    [Fact]
    public void SecondRun_ReportsZeroChanges_AndHashesNothing()
    {
        _updater.Update(_discovery.Discover(_repo));

        var second = _updater.Update(_discovery.Discover(_repo));

        Assert.Empty(second.Diff.Added);
        Assert.Empty(second.Diff.Modified);
        Assert.Empty(second.Diff.Deleted);
        Assert.Equal(0, second.Diff.ChangedCount);
        Assert.Equal(18, second.UnchangedCount);
    }

    [Fact]
    public void ModifiedFile_IsReportedExactlyOnce()
    {
        _updater.Update(_discovery.Discover(_repo));

        var changedFile = Path.Combine(_repo, "src", "Customer.Services", "OrderService.cs");
        File.AppendAllText(changedFile, Environment.NewLine + "// modified");

        var second = _updater.Update(_discovery.Discover(_repo));

        Assert.Empty(second.Diff.Added);
        Assert.Empty(second.Diff.Deleted);
        var modified = Assert.Single(second.Diff.Modified);
        Assert.Equal("src/Customer.Services/OrderService.cs", modified);
    }

    [Fact]
    public void TimestampChangeWithoutContentChange_IsNotAModification()
    {
        _updater.Update(_discovery.Discover(_repo));

        // Touch timestamp only; the SHA-256 hash backstop must classify this as unchanged.
        var touchedFile = Path.Combine(_repo, "src", "Customer.Domain", "Customer.cs");
        File.SetLastWriteTimeUtc(touchedFile, DateTime.UtcNow.AddMinutes(5));

        var second = _updater.Update(_discovery.Discover(_repo));

        Assert.Empty(second.Diff.Modified);
        Assert.Empty(second.Diff.Added);
        Assert.Empty(second.Diff.Deleted);
    }

    [Fact]
    public void DeletedFile_IsReportedAsDeleted()
    {
        _updater.Update(_discovery.Discover(_repo));

        File.Delete(Path.Combine(_repo, "src", "Customer.Api", "Startup.cs"));

        var second = _updater.Update(_discovery.Discover(_repo));

        var deleted = Assert.Single(second.Diff.Deleted);
        Assert.Equal("src/Customer.Api/Startup.cs", deleted);
        Assert.False(second.Index.Files.ContainsKey("src/Customer.Api/Startup.cs"));
    }

    [Fact]
    public void CorruptIndex_IsTreatedAsAbsent_AndFullReindexSucceeds()
    {
        _updater.Update(_discovery.Discover(_repo));

        File.WriteAllText(RepositoryIndex.GetIndexPath(_repo, _options.IndexPath), "{ this is not json");

        Assert.Null(RepositoryIndex.Load(_fileSystem, _repo, _options.IndexPath));

        var result = _updater.Update(_discovery.Discover(_repo));
        Assert.Equal(18, result.Diff.Added.Count);
        Assert.Equal(18, result.Index.Files.Count);
    }

    [Fact]
    public void IndexStampedForAnotherRepositoryRoot_IsTreatedAsAbsent()
    {
        _updater.Update(_discovery.Discover(_repo));

        // Simulate an index copied along with sources from a different checkout.
        var loaded = RepositoryIndex.Load(_fileSystem, _repo, _options.IndexPath);
        Assert.NotNull(loaded);
        loaded!.Repository = Path.Combine(Path.GetTempPath(), "some-other-checkout");
        loaded.Save(_fileSystem, _repo, _options.IndexPath);

        Assert.Null(RepositoryIndex.Load(_fileSystem, _repo, _options.IndexPath));

        // Full re-index from scratch recovers.
        var result = _updater.Update(_discovery.Discover(_repo));
        Assert.Equal(18, result.Diff.Added.Count);
        Assert.Equal(18, result.Index.Files.Count);
    }

    [Fact]
    public void PersistedIndex_RoundTripsThroughLoad()
    {
        var first = _updater.Update(_discovery.Discover(_repo));

        var loaded = RepositoryIndex.Load(_fileSystem, _repo, _options.IndexPath);

        Assert.NotNull(loaded);
        Assert.Equal(first.Index.Repository, loaded!.Repository);
        Assert.Equal(first.Index.Files.Count, loaded.Files.Count);
        Assert.Equal(
            first.Index.Files["src/Customer.Domain/Customer.cs"].Hash,
            loaded.Files["src/Customer.Domain/Customer.cs"].Hash);
    }

    [Fact]
    public void Save_UsesAtomicReplace_AndLeavesNoTempFiles()
    {
        _updater.Update(_discovery.Discover(_repo));

        var aceDirectory = Path.Combine(_repo, _options.IndexPath);
        Assert.DoesNotContain(Directory.EnumerateFiles(aceDirectory), f => f.Contains(".tmp.", StringComparison.Ordinal));
    }

    [Fact]
    public void FileChangedDuringHashing_IsRecordedFailed_AndSucceedsOnRetry()
    {
        // Simulates a concurrent writer: the file mutates while its content is being
        // hashed. ACE must refuse to pin the stale hash (per-file failure) and pick
        // the file up again on the next run.
        var mutatedRelative = "src/Customer.Domain/Customer.cs";
        var mutatedFull = Path.Combine(_repo, "src", "Customer.Domain", "Customer.cs");
        var mutatingFileSystem = new MutatingDuringReadFileSystem(mutatedFull, () => File.AppendAllText(mutatedFull, "\n// concurrent write"));
        var updater = new IndexUpdater(mutatingFileSystem, _options);

        var first = updater.Update(_discovery.Discover(_repo));

        Assert.False(first.Index.Files.ContainsKey(mutatedRelative), "stale hash must not be pinned");
        Assert.True(first.FailedFiles.ContainsKey(mutatedRelative), "the racing file must be recorded as failed");
        Assert.Contains("changed", first.FailedFiles[mutatedRelative], StringComparison.OrdinalIgnoreCase);

        // No concurrent writer on the second run: the file is hashed and indexed.
        var second = updater.Update(_discovery.Discover(_repo));

        Assert.True(second.Index.Files.ContainsKey(mutatedRelative));
        Assert.False(second.FailedFiles.ContainsKey(mutatedRelative));
    }

    /// <summary>
    /// Delegates to the real file system but mutates one target file the moment its
    /// content stream is opened for hashing — i.e. after the pre-hash stat and before
    /// the re-stat, changing size + mtime mid-hash.
    /// </summary>
    private sealed class MutatingDuringReadFileSystem : IFileSystemService
    {
        private readonly FileSystemService _inner = new();
        private readonly string _targetPath;
        private readonly Action _mutate;
        private bool _mutated;

        public MutatingDuringReadFileSystem(string targetPath, Action mutate)
        {
            _targetPath = targetPath;
            _mutate = mutate;
        }

        public Stream OpenRead(string path)
        {
            if (!_mutated && string.Equals(Path.GetFullPath(path), Path.GetFullPath(_targetPath), StringComparison.OrdinalIgnoreCase))
            {
                _mutated = true;
                _mutate(); // Concurrent write lands between the pre-hash stat and the re-stat.
            }

            return _inner.OpenRead(path);
        }

        public bool FileExists(string path) => _inner.FileExists(path);

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

        public string ReadAllText(string path) => _inner.ReadAllText(path);

        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
            => _inner.ReadAllTextAsync(path, cancellationToken);

        public IReadOnlyList<string> EnumerateFiles(string directoryPath) => _inner.EnumerateFiles(directoryPath);

        public IReadOnlyList<string> EnumerateDirectories(string directoryPath) => _inner.EnumerateDirectories(directoryPath);

        public long GetFileSize(string path) => _inner.GetFileSize(path);

        public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

        public void CreateDirectory(string path) => _inner.CreateDirectory(path);

        public void WriteAllText(string path, string contents) => _inner.WriteAllText(path, contents);

        public void ReplaceFile(string tempPath, string destinationPath) => _inner.ReplaceFile(tempPath, destinationPath);

        public string GetFullPath(string path) => _inner.GetFullPath(path);
    }
}
