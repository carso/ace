using Microsoft.Extensions.Configuration;

namespace Ace.Core.Configuration;

/// <summary>
/// Creates <see cref="AceOptions"/> for both the CLI and the MCP server.
/// Layering (later sources win):
/// <list type="number">
///   <item>Built-in defaults</item>
///   <item><c>ace.json</c> in the repository root (optional; values under an <c>"ace"</c> section)</item>
///   <item><c>ACE__*</c> environment variables mapped into the <c>ace</c> section
///         (e.g. <c>ACE__INDEXPATH</c>, <c>ACE__MAXPARALLELISM</c>, <c>ACE__ENABLEGITANALYSIS</c>,
///         <c>ACE__EXCLUSIONPATTERNS__0</c>)</item>
/// </list>
/// </summary>
public static class AceOptionsFactory
{
    /// <summary>Well-known file name for per-repository ACE configuration.</summary>
    public const string ConfigFileName = "ace.json";

    /// <summary>
    /// Loads ACE options. <paramref name="repositoryPath"/> selects the repository whose
    /// <c>ace.json</c> is consulted; pass <c>null</c> for defaults + environment only.
    /// Never throws for missing/invalid configuration files — invalid files are ignored
    /// so ACE keeps working with defaults.
    /// </summary>
    public static AceOptions Load(string? repositoryPath = null)
    {
        var options = new AceOptions();

        var builder = new ConfigurationBuilder();

        if (!string.IsNullOrWhiteSpace(repositoryPath))
        {
            var configPath = Path.Combine(Path.GetFullPath(repositoryPath), ConfigFileName);
            if (File.Exists(configPath))
            {
                try
                {
                    builder.AddJsonFile(configPath, optional: true, reloadOnChange: false);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
                {
                    // Malformed/unreadable ace.json must not break ACE; continue with defaults.
                }
            }
        }

        // ACE__INDEXPATH -> ace:indexPath, ACE__MAXPARALLELISM -> ace:maxParallelism, ...
        builder.AddEnvironmentVariables();

        try
        {
            var section = builder.Build().GetSection(AceOptions.SectionName);
            if (section.Exists())
            {
                section.Bind(options);

                // The configuration binder merges into existing collections; ACE wants
                // replacement semantics for pattern lists.
                OverrideStringList(section, "exclusionPatterns", options.ExclusionPatterns);
                OverrideStringList(section, "sensitiveFilePatterns", options.SensitiveFilePatterns);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unbindable or malformed configuration falls back to defaults.
        }

        options.Normalize();
        return options;
    }

    private static void OverrideStringList(IConfigurationSection section, string key, List<string> target)
    {
        var child = section.GetSection(key);
        if (!child.Exists())
        {
            return;
        }

        target.Clear();
        foreach (var item in child.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(item.Value))
            {
                target.Add(item.Value);
            }
        }
    }
}
