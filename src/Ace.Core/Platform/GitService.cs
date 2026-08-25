namespace Ace.Core.Platform;

/// <summary>
/// Default <see cref="IGitService"/>: shells out to the git CLI via <see cref="IProcessService"/>.
/// Never throws — git missing, non-repositories and bad ranges all yield structured fallbacks.
/// </summary>
public sealed class GitService : IGitService
{
    private readonly IProcessService _processService;

    public GitService(IProcessService processService)
    {
        _processService = processService;
    }

    public async Task<GitStatusResult> GetStatusAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(repositoryPath, ["status", "--porcelain"], cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return new GitStatusResult(Available: false, IsRepository: false, [], Error: "git executable not found");
        }

        if (!result.Value.Process.Success)
        {
            return IsRepositoryError(result.Value.Process.StandardError)
                ? new GitStatusResult(Available: true, IsRepository: false, [], Error: "not a git repository")
                : new GitStatusResult(Available: true, IsRepository: false, [], Error: result.Value.Process.StandardError.Trim());
        }

        return new GitStatusResult(Available: true, IsRepository: true, ParsePorcelain(result.Value.Process.StandardOutput));
    }

    public async Task<GitDiffResult> GetDiffFilesAsync(string repositoryPath, string range, CancellationToken cancellationToken = default)
    {
        var result = await RunGitAsync(repositoryPath, ["diff", "--name-only", range], cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return new GitDiffResult(Available: false, IsRepository: false, range, [], Error: "git executable not found");
        }

        if (!result.Value.Process.Success)
        {
            return IsRepositoryError(result.Value.Process.StandardError)
                ? new GitDiffResult(Available: true, IsRepository: false, range, [], Error: "not a git repository")
                : new GitDiffResult(Available: true, IsRepository: false, range, [], Error: result.Value.Process.StandardError.Trim());
        }

        var files = result.Value.Process.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Unquote)
            .ToList();

        return new GitDiffResult(Available: true, IsRepository: true, range, files);
    }

    private async Task<(ProcessResult Process, string WorkingDirectory)?> RunGitAsync(
        string repositoryPath,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var workingDirectory = Path.GetFullPath(repositoryPath);
        var result = await _processService
            .RunAsync("git", arguments, workingDirectory, timeout: TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        // ProcessService returns ExitCode == -1 when the executable could not be started.
        if (result.ExitCode == -1 && !result.TimedOut)
        {
            return null;
        }

        return (result, workingDirectory);
    }

    private static bool IsRepositoryError(string stderr)
        => stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses <c>git status --porcelain</c> output. Each line is "XY path" (porcelain v1);
    /// renamed/copied entries use "orig -> new" and the new path is reported.
    /// </summary>
    internal static IReadOnlyList<string> ParsePorcelain(string output)
    {
        var files = new List<string>();

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length < 4)
            {
                continue;
            }

            // Porcelain v1: two status characters, one space, then the path.
            var path = line[3..].Trim();

            var arrowIndex = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrowIndex >= 0)
            {
                path = path[(arrowIndex + 4)..];
            }

            path = Unquote(path);
            if (path.Length > 0)
            {
                files.Add(path);
            }
        }

        return files;
    }

    private static string Unquote(string path)
        => path.Length >= 2 && path[0] == '"' && path[^1] == '"'
            ? path[1..^1]
            : path;
}
