using System.Text.RegularExpressions;

namespace Ace.Core.Security;

/// <summary>
/// Matches file paths against configurable sensitive-file patterns (SR-006).
/// Patterns are glob-style and case-insensitive: literal names (<c>.env</c>,
/// <c>secrets.json</c>) match a file name or path segment exactly, wildcards
/// (<c>*.key</c>, <c>config/*.pem</c>) match file names or normalized paths.
/// </summary>
public sealed class SensitiveFileRules
{
    private readonly IReadOnlyList<SensitivePattern> _patterns;

    public SensitiveFileRules(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        _patterns = patterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new SensitivePattern(p.Trim()))
            .ToList();
    }

    /// <summary>The raw patterns these rules were built from.</summary>
    public IReadOnlyList<string> Patterns => _patterns.Select(p => p.Raw).ToList();

    /// <summary>True when <paramref name="filePath"/> matches any sensitive pattern.</summary>
    public bool IsSensitive(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || _patterns.Count == 0)
        {
            return false;
        }

        var normalized = filePath.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        if (fileName.Length == 0)
        {
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pattern in _patterns)
        {
            if (!pattern.HasWildcard)
            {
                // Literal patterns match the file name or any path segment exactly.
                if (segments.Any(s => string.Equals(s, pattern.Raw, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            else if (pattern.Regex.IsMatch(fileName) || pattern.Regex.IsMatch(normalized))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class SensitivePattern
    {
        public SensitivePattern(string raw)
        {
            Raw = raw;
            HasWildcard = raw.Contains('*') || raw.Contains('?');
            Regex = HasWildcard ? Compile(raw) : null!;
        }

        public string Raw { get; }

        public bool HasWildcard { get; }

        public Regex Regex { get; }

        private static Regex Compile(string pattern)
        {
            var escaped = Regex.Escape(pattern)
                .Replace(@"\*", ".*", StringComparison.Ordinal)
                .Replace(@"\?", ".", StringComparison.Ordinal);

            return new Regex(
                $"^{escaped}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
    }
}
