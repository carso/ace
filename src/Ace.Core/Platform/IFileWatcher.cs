namespace Ace.Core.Platform;

/// <summary>Arguments for <see cref="IFileWatcher.Changed"/> events.</summary>
public sealed class FileWatcherEventArgs(string fullPath, WatcherChangeTypes changeType) : EventArgs
{
    /// <summary>Full path of the affected file or directory.</summary>
    public string FullPath { get; } = fullPath;

    public WatcherChangeTypes ChangeType { get; } = changeType;
}

/// <summary>
/// Repository file-watch abstraction (SRS §13). Consumers (e.g. the incremental index)
/// subscribe to <see cref="Changed"/>; implementations must be safe to Start/Stop repeatedly.
/// </summary>
public interface IFileWatcher : IDisposable
{
    event EventHandler<FileWatcherEventArgs>? Changed;

    /// <summary>True while watching.</summary>
    bool IsRunning { get; }

    /// <summary>Begins watching <paramref name="rootPath"/> recursively.</summary>
    void Start(string rootPath);

    void Stop();
}
