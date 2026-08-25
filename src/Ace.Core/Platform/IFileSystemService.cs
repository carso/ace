namespace Ace.Core.Platform;

/// <summary>
/// File system abstraction so platform-specific behavior is isolated behind an interface (SRS §13)
/// and core logic stays testable.
/// </summary>
public interface IFileSystemService
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    Stream OpenRead(string path);

    string ReadAllText(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Full paths of files directly inside <paramref name="directoryPath"/> (non-recursive).</summary>
    IReadOnlyList<string> EnumerateFiles(string directoryPath);

    /// <summary>Full paths of subdirectories directly inside <paramref name="directoryPath"/> (non-recursive).</summary>
    IReadOnlyList<string> EnumerateDirectories(string directoryPath);

    long GetFileSize(string path);

    DateTime GetLastWriteTimeUtc(string path);

    void CreateDirectory(string path);

    void WriteAllText(string path, string contents);

    /// <summary>Atomic-ish replace: write <paramref name="tempPath"/> then move over <paramref name="destinationPath"/>.</summary>
    void ReplaceFile(string tempPath, string destinationPath);

    string GetFullPath(string path);
}
