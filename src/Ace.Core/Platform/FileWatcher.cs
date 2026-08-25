namespace Ace.Core.Platform;

/// <summary>
/// Default <see cref="IFileWatcher"/>: a thin wrapper around <see cref="FileSystemWatcher"/>.
/// Events are raised on thread-pool threads; subscribers must be thread-safe.
/// </summary>
public sealed class FileWatcher : IFileWatcher
{
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;

    public event EventHandler<FileWatcherEventArgs>? Changed;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _watcher?.EnableRaisingEvents == true;
            }
        }
    }

    public void Start(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        lock (_gate)
        {
            StopCore();

            var watcher = new FileSystemWatcher(rootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.CreationTime,
            };

            watcher.Created += OnEvent;
            watcher.Changed += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += OnRenamed;

            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private void StopCore()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnEvent;
        _watcher.Changed -= OnEvent;
        _watcher.Deleted -= OnEvent;
        _watcher.Renamed -= OnRenamed;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
        => Changed?.Invoke(this, new FileWatcherEventArgs(e.FullPath, e.ChangeType));

    private void OnRenamed(object sender, RenamedEventArgs e)
        => Changed?.Invoke(this, new FileWatcherEventArgs(e.FullPath, e.ChangeType));
}
