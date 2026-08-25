using System.Text.Json;
using Ace.Core.Models;
using Ace.Core.Security;

namespace Ace.Mcp.Server.Tools;

/// <summary>
/// Shared plumbing for every ACE MCP tool: input validation through PathGuard
/// (SR-004/SR-005), AceJson serialization of results (§9), and structured JSON
/// errors instead of thrown exceptions (§17) so clients always receive
/// <c>{"error":{"code","message"}}</c> rather than a protocol-level failure.
/// </summary>
public static class ToolInvoker
{
    /// <summary>API version label exposed via ace_status (SRS §21).</summary>
    public const string ApiVersion = "ACE MCP v1";

    /// <summary>Runs a tool operation, converting every failure into a structured JSON error.</summary>
    public static async Task<string> ExecuteAsync(string repositoryPath, Func<string, Task<object>> operation)
    {
        try
        {
            var root = NormalizeRepositoryPath(repositoryPath);
            var result = await operation(root).ConfigureAwait(false);
            return result as string ?? JsonSerializer.Serialize(result, AceJson.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SerializeError(ex);
        }
    }

    /// <summary>
    /// Normalizes the repository root (full path, must exist). Relative roots are
    /// rejected with a structured invalid_argument error instead of being resolved
    /// against the server process working directory. A malformed or missing root is
    /// reported as a structured error, never an unhandled crash.
    /// </summary>
    public static string NormalizeRepositoryPath(string repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new ArgumentException("repositoryPath is required.", nameof(repositoryPath));
        }

        if (!Path.IsPathRooted(repositoryPath))
        {
            throw new ArgumentException(
                "repositoryPath must be an absolute path; relative paths are not resolved against the server working directory.",
                nameof(repositoryPath));
        }

        string root;
        try
        {
            root = Path.GetFullPath(repositoryPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            throw new PathSecurityException($"repositoryPath could not be normalized: {repositoryPath}", ex);
        }

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository root does not exist: {root}");
        }

        return root;
    }

    /// <summary>
    /// Validates every changed-file candidate against the repository root via PathGuard.
    /// Relative paths resolve against the root; escaping paths raise PathSecurityException.
    /// </summary>
    public static IReadOnlyList<string> ValidateChangedFiles(string repositoryRoot, IReadOnlyList<string> changedFiles)
    {
        if (changedFiles.Count == 0)
        {
            throw new ArgumentException("changedFiles must contain at least one path.", nameof(changedFiles));
        }

        return changedFiles
            .Select(file => PathGuard.EnsureWithinRoot(repositoryRoot, file))
            .ToList();
    }

    private static string SerializeError(Exception exception)
    {
        var payload = new
        {
            error = new
            {
                code = GetErrorCode(exception),
                message = exception.Message,
            },
        };

        return JsonSerializer.Serialize(payload, AceJson.Options);
    }

    private static string GetErrorCode(Exception exception) => exception switch
    {
        PathSecurityException => "path_security",
        DirectoryNotFoundException => "repository_not_found",
        ArgumentException => "invalid_argument",
        JsonException => "invalid_argument",
        _ => "internal_error",
    };
}
