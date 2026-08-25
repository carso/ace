using System.Collections.Concurrent;
using Ace.Core.Configuration;
using Ace.Core.Models;
using Ace.Core.Platform;
using Ace.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ace.Core.Discovery;

/// <summary>File classification buckets used by discovery and indexing (FR-001).</summary>
public static class FileCategory
{
    public const string Source = "source";
    public const string Project = "project";
    public const string Solution = "solution";
    public const string Config = "config";
    public const string Test = "test";
    public const string Manifest = "manifest";
    public const string Doc = "doc";
    public const string Other = "other";
}

/// <summary>A file discovered during repository discovery, with its classification bucket.</summary>
/// <param name="RelativePath">Repository-relative path using forward slashes.</param>
/// <param name="FullPath">Absolute path on disk.</param>
/// <param name="Category">Classification bucket (<see cref="FileCategory"/>).</param>
public sealed record DiscoveredFile(string RelativePath, string FullPath, string Category);

/// <summary>Result of repository discovery: categorized files plus structured context (FR-002).</summary>
public sealed record DiscoveryResult
{
    public required string RootPath { get; init; }

    public required IReadOnlyList<DiscoveredFile> Files { get; init; }

    public required RepositoryContext Context { get; init; }

    public IReadOnlyList<DiscoveredFile> ByCategory(string category)
        => Files.Where(f => f.Category == category).ToList();
}

/// <summary>
/// Recursive repository discovery (FR-001/FR-002). Enumerates a repository root,
/// pruning excluded directory names BEFORE descending, skipping sensitive files
/// (SR-006) and the ACE index folder itself, classifying files into buckets and
/// detecting languages, frameworks, build systems and test projects.
/// </summary>
public sealed class RepositoryDiscovery
{
    private static readonly Dictionary<string, string> SourceLanguageByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#",
        [".ts"] = "TypeScript",
        [".tsx"] = "TypeScript",
        [".js"] = "JavaScript",
        [".jsx"] = "JavaScript",
        [".py"] = "Python",
        [".java"] = "Java",
        [".sql"] = "SQL",
    };

    private static readonly HashSet<string> ConfigExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".json", ".xml", ".config", ".yaml", ".yml", ".ini", ".toml", ".props", ".targets",
    };

    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".rst",
    };

    private static readonly string[] TestFrameworkMarkers =
    [
        "xunit", "nunit", "mstest", "microsoft.net.test.sdk",
    ];

    private readonly IFileSystemService _fileSystem;
    private readonly AceOptions _options;
    private readonly SensitiveFileRules _sensitiveRules;
    private readonly ILogger<RepositoryDiscovery> _logger;

    public RepositoryDiscovery(
        IFileSystemService fileSystem,
        AceOptions options,
        SensitiveFileRules? sensitiveRules = null,
        ILogger<RepositoryDiscovery>? logger = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sensitiveRules = sensitiveRules ?? new SensitiveFileRules(options.SensitiveFilePatterns);
        _logger = logger ?? NullLogger<RepositoryDiscovery>.Instance;
    }

    /// <summary>Discovers and classifies all indexable files under <paramref name="rootPath"/>.</summary>
    public DiscoveryResult Discover(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = _fileSystem.GetFullPath(rootPath);
        if (!_fileSystem.DirectoryExists(root))
        {
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        }

        var excludedDirs = new HashSet<string>(
            _options.ExclusionPatterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var indexDirName = Path.GetFileName(_fileSystem.GetFullPath(Path.Combine(root, _options.IndexPath)));

        var files = new ConcurrentBag<DiscoveredFile>();
        EnumerateDirectory(root, root, excludedDirs, indexDirName, files);

        var ordered = files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        var context = BuildContext(root, ordered);
        return new DiscoveryResult { RootPath = root, Files = ordered, Context = context };
    }

    private void EnumerateDirectory(
        string root,
        string directory,
        HashSet<string> excludedDirs,
        string indexDirName,
        ConcurrentBag<DiscoveredFile> results)
    {
        IReadOnlyList<string> subDirectories;
        IReadOnlyList<string> entries;
        try
        {
            subDirectories = _fileSystem.EnumerateDirectories(directory);
            entries = _fileSystem.EnumerateFiles(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning("Skipping unreadable directory {Directory}: {Error}", directory, ex.Message);
            return;
        }

        foreach (var subDir in subDirectories)
        {
            var name = Path.GetFileName(subDir);
            // Prune excluded directory names BEFORE descending (FR-001).
            if (excludedDirs.Contains(name) || string.Equals(name, indexDirName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Containment: never follow reparse points (junctions/symlinks); a link whose
            // final physical target escapes the repository root is an indexing escape (SR-002/005).
            if (EscapesRoot(root, subDir))
            {
                _logger.LogWarning("Skipping link directory {Directory}: target leaves the repository root", subDir);
                continue;
            }

            EnumerateDirectory(root, subDir, excludedDirs, indexDirName, results);
        }

        foreach (var file in entries)
        {
            var relative = ToRelativePath(root, file);
            if (_sensitiveRules.IsSensitive(relative))
            {
                continue;
            }

            var category = Categorize(relative);
            results.Add(new DiscoveredFile(relative, file, category));
        }
    }

    /// <summary>
    /// True when a directory entry is a reparse point, or its final resolved link target
    /// falls outside <paramref name="root"/>. Unreadable links are treated as escapes.
    /// </summary>
    private static bool EscapesRoot(string root, string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }

            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            return resolved is not null && !PathGuard.IsWithinRoot(root, resolved.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Classifies a repository-relative path into a discovery bucket.</summary>
    public string Categorize(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        var extension = Path.GetExtension(normalized);

        if (fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase))
        {
            return FileCategory.Manifest;
        }

        if (string.Equals(fileName, "appsettings.json", StringComparison.OrdinalIgnoreCase))
        {
            return FileCategory.Config;
        }

        return extension.ToLowerInvariant() switch
        {
            ".csproj" or ".fsproj" or ".vbproj" => FileCategory.Project,
            ".sln" => FileCategory.Solution,
            ".cs" or ".ts" or ".tsx" or ".js" or ".jsx" or ".py" or ".java" or ".sql"
                => ContainsTestSegment(normalized) ? FileCategory.Test : FileCategory.Source,
            ".cshtml" or ".razor" => FileCategory.Source,
            ".md" or ".txt" or ".rst" => FileCategory.Doc,
            _ when ConfigExtensions.Contains(extension) => FileCategory.Config,
            _ => FileCategory.Other,
        };
    }

    private static bool ContainsTestSegment(string normalizedRelativePath)
    {
        var segments = normalizedRelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Inspect directory segments only (the file name itself may legitimately contain "test").
        return segments[..^1].Any(s => s.Contains("test", StringComparison.OrdinalIgnoreCase));
    }

    private RepositoryContext BuildContext(string root, IReadOnlyList<DiscoveredFile> files)
    {
        var languages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var frameworks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var buildSystems = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencySystems = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var testProjects = new ConcurrentBag<string>();

        foreach (var file in files)
        {
            if (SourceLanguageByExtension.TryGetValue(Path.GetExtension(file.RelativePath), out var language))
            {
                languages.Add(language);
            }

            if (file.Category is FileCategory.Project or FileCategory.Solution)
            {
                buildSystems.Add("MSBuild");
                dependencySystems.Add("NuGet");
            }

            if (file.RelativePath.EndsWith("package.json", StringComparison.OrdinalIgnoreCase))
            {
                buildSystems.Add("npm");
                dependencySystems.Add("npm");
            }
        }

        // Project inspection is I/O bound; use bounded parallelism (FR-001, §4.5 style fast paths).
        var projectFiles = files.Where(f => f.Category == FileCategory.Project).ToList();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = AceOptions.ClampParallelism(_options.MaxParallelism),
        };
        var frameworkLock = new object();

        Parallel.ForEach(projectFiles, parallelOptions, file =>
        {
            InspectProjectFile(root, file, frameworks, frameworkLock, testProjects);
        });

        var projectCount = files.Count(f => f.Category == FileCategory.Project);

        return new RepositoryContext
        {
            RepositoryPath = root,
            FileCount = files.Count,
            SourceFileCount = files.Count(f => f.Category is FileCategory.Source or FileCategory.Test),
            ProjectCount = projectCount,
            Languages = languages.ToList(),
            Frameworks = frameworks.ToList(),
            BuildSystems = buildSystems.ToList(),
            TestProjects = testProjects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            DependencySystems = dependencySystems.ToList(),
        };
    }

    private void InspectProjectFile(
        string root,
        DiscoveredFile projectFile,
        SortedSet<string> frameworks,
        object frameworkLock,
        ConcurrentBag<string> testProjects)
    {
        var projectName = Path.GetFileNameWithoutExtension(projectFile.RelativePath);
        string content;
        try
        {
            content = _fileSystem.ReadAllText(projectFile.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read project file {File}: {Error}", projectFile.RelativePath, ex.Message);
            return;
        }

        if (content.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
        {
            lock (frameworkLock)
            {
                frameworks.Add("ASP.NET Core");
            }
        }

        if (content.Contains("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase))
        {
            lock (frameworkLock)
            {
                frameworks.Add("ASP.NET Core");
            }
        }

        var looksLikeTestProject =
            projectName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
            projectName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
            TestFrameworkMarkers.Any(marker => content.Contains(marker, StringComparison.OrdinalIgnoreCase));

        if (looksLikeTestProject)
        {
            testProjects.Add(projectName);
        }
    }

    private static string ToRelativePath(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath).Replace('\\', '/');
}
