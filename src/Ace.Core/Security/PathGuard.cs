namespace Ace.Core.Security;

/// <summary>
/// Thrown when a path fails ACE containment validation (SR-002/004/005).
/// Callers should surface this as a structured error to the requesting agent, never as a stack trace.
/// </summary>
public sealed class PathSecurityException : Exception
{
    public PathSecurityException(string message)
        : base(message)
    {
    }

    public PathSecurityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Path containment guard — the single choke point every ACE entry point must pass paths
/// through before touching the file system (SR-002, SR-004, SR-005).
/// </summary>
public static class PathGuard
{
    /// <summary>
    /// Validates that <paramref name="candidatePath"/> resolves to a location inside
    /// <paramref name="rootPath"/>. Relative candidates are resolved against the root.
    /// Canonicalizes both paths, rejects <c>..</c> traversal escapes and UNC/sibling-prefix
    /// tricks (case-insensitive containment on Windows), and returns the validated
    /// absolute path. Reparse-point links (junctions/symlinks) are additionally resolved
    /// to their final physical target, which must also stay within the root (SR-002/005).
    /// </summary>
    /// <exception cref="PathSecurityException">The candidate escapes the root or cannot be normalized.</exception>
    public static string EnsureWithinRoot(string rootPath, string candidatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);

        if (candidatePath.Contains('\0'))
        {
            throw new PathSecurityException("Path contains invalid characters.");
        }

        string fullRoot;
        string fullCandidate;
        try
        {
            fullRoot = Path.GetFullPath(rootPath);
            fullCandidate = Path.GetFullPath(candidatePath, fullRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException or PathTooLongException)
        {
            throw new PathSecurityException($"Path could not be normalized: {candidatePath}", ex);
        }

        if (!IsWithinRoot(fullRoot, fullCandidate))
        {
            throw new PathSecurityException(
                $"Path '{candidatePath}' is outside the allowed repository root.");
        }

        // Link containment: the final physical target of any junction/symlink along the
        // way must also stay inside the root; a link that points outside is an escape.
        var physicalTarget = ResolveLinkTargets(fullCandidate);
        if (!IsWithinRoot(fullRoot, physicalTarget))
        {
            throw new PathSecurityException(
                $"Path '{candidatePath}' is a link whose target is outside the allowed repository root.");
        }

        return fullCandidate;
    }

    /// <summary>
    /// Resolves reparse-point links (junctions/symlinks) in <paramref name="fullPath"/> to
    /// their final physical target. Every existing path component is checked individually
    /// (a file is not itself a reparse point when it merely lives inside a junction, so
    /// parents must be resolved component-wise). Non-existent trailing segments are
    /// re-appended to the resolved prefix.
    /// </summary>
    public static string ResolveLinkTargets(string fullPath)
    {
        var resolved = Path.GetPathRoot(fullPath) ?? fullPath;
        var remaining = fullPath[resolved.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        foreach (var segment in remaining)
        {
            var candidate = Path.Combine(resolved, segment);
            try
            {
                FileSystemInfo? info = null;
                if (Directory.Exists(candidate))
                {
                    info = new DirectoryInfo(candidate);
                }
                else if (File.Exists(candidate))
                {
                    info = new FileInfo(candidate);
                }

                resolved = info?.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A broken or unreadable link cannot be verified to stay inside the root.
                throw new PathSecurityException($"Path link target could not be resolved: {candidate}", ex);
            }
        }

        return resolved;
    }

    /// <summary>True when <paramref name="candidate"/> equals or is contained within <paramref name="root"/>.</summary>
    public static bool IsWithinRoot(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedRoot.Length == 0)
        {
            return false;
        }

        if (string.Equals(candidate, normalizedRoot, comparison))
        {
            return true;
        }

        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, comparison);
    }
}
