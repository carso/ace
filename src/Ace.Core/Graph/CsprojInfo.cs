using System.Xml.Linq;

namespace Ace.Core.Graph;

/// <summary>A <c>&lt;ProjectReference&gt;</c> parsed from a csproj file.</summary>
/// <param name="Include">Raw Include attribute value.</param>
/// <param name="ProjectName">Referenced project name (file name without extension).</param>
/// <param name="NormalizedInclude">Include with forward slashes.</param>
public sealed record ProjectReferenceInfo(string Include, string ProjectName, string NormalizedInclude);

/// <summary>A <c>&lt;PackageReference&gt;</c> parsed from a csproj file.</summary>
/// <param name="Include">Package id.</param>
/// <param name="Version">Declared version, when present.</param>
public sealed record PackageReferenceInfo(string Include, string? Version);

/// <summary>
/// Lightweight, MSBuild-free view of a csproj file: enough to build project-level
/// DEPENDS_ON edges (FR-004). Parsed with plain XML, tolerant of malformed content.
/// </summary>
public sealed record CsprojInfo
{
    public required string ProjectName { get; init; }

    /// <summary>Repository-relative csproj path, forward slashes.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Repository-relative directory of the csproj, forward slashes, no trailing slash.</summary>
    public required string RelativeDirectory { get; init; }

    public IReadOnlyList<ProjectReferenceInfo> ProjectReferences { get; init; } = [];

    public IReadOnlyList<PackageReferenceInfo> PackageReferences { get; init; } = [];

    /// <summary>Parses csproj XML content; returns null when the content is not parseable.</summary>
    public static CsprojInfo? TryParse(string relativePath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalizedPath = relativePath.Replace('\\', '/');

        try
        {
            var document = XDocument.Parse(content, LoadOptions.PreserveWhitespace);

            var projectReferences = document.Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v =>
                {
                    var normalized = v!.Replace('\\', '/');
                    return new ProjectReferenceInfo(v, Path.GetFileNameWithoutExtension(normalized), normalized);
                })
                .ToList();

            var packageReferences = document.Descendants("PackageReference")
                .Select(e => (
                    Include: e.Attribute("Include")?.Value,
                    Version: e.Attribute("Version")?.Value ?? e.Element("Version")?.Value))
                .Where(t => !string.IsNullOrWhiteSpace(t.Include))
                .Select(t => new PackageReferenceInfo(t.Include!, t.Version))
                .ToList();

            var directory = normalizedPath.Contains('/')
                ? normalizedPath[..normalizedPath.LastIndexOf('/')]
                : string.Empty;

            return new CsprojInfo
            {
                ProjectName = Path.GetFileNameWithoutExtension(normalizedPath),
                RelativePath = normalizedPath,
                RelativeDirectory = directory,
                ProjectReferences = projectReferences,
                PackageReferences = packageReferences,
            };
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return null;
        }
    }
}
