namespace Ace.Core.Platform;

/// <summary>Default managed implementation of <see cref="IFileSystemService"/> (System.IO).</summary>
public sealed class FileSystemService : IFileSystemService
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public Stream OpenRead(string path) => File.OpenRead(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
        => File.ReadAllTextAsync(path, cancellationToken);

    public IReadOnlyList<string> EnumerateFiles(string directoryPath)
        => Directory.EnumerateFiles(directoryPath).ToList();

    public IReadOnlyList<string> EnumerateDirectories(string directoryPath)
        => Directory.EnumerateDirectories(directoryPath).ToList();

    public long GetFileSize(string path) => new FileInfo(path).Length;

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    public void ReplaceFile(string tempPath, string destinationPath)
        => File.Move(tempPath, destinationPath, overwrite: true);

    public string GetFullPath(string path) => Path.GetFullPath(path);
}
