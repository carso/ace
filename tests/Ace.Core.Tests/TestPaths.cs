namespace Ace.Core.Tests;

/// <summary>
/// Locates the shared SampleRepo fixture (tests/TestAssets/SampleRepo) and provides
/// throw-away copies for tests that need to modify or index files. SampleRepo is a
/// content asset: it is not part of Ace.sln and never compiled.
/// </summary>
public static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string SampleRepo => Path.Combine(RepoRoot, "tests", "TestAssets", "SampleRepo");

    /// <summary>Copies SampleRepo into a fresh temp directory; caller must delete it.</summary>
    public static string CreateTempCopyOfSampleRepo()
    {
        var temp = Path.Combine(Path.GetTempPath(), "ace-tests", Guid.NewGuid().ToString("N"));
        CopyDirectory(SampleRepo, temp);
        return temp;
    }

    public static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ace.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate Ace.sln above the test binary.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var subDirectory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(subDirectory, Path.Combine(destination, Path.GetFileName(subDirectory)));
        }
    }
}
