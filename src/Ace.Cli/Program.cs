// ACE CLI — human/script adapter over AceEngineFacade (SRS §20).
//
// The CLI exposes the same ACE Core intelligence as the MCP server: every verb
// delegates to AceEngineFacade. Unlike the MCP server (whose stdout is reserved
// for JSON-RPC framing), the CLI writes reports to stdout freely.
//
// Verbs: init, index, status, analyze, impact, graph, context, tests, regression.
// Every verb supports --json to print the raw camelCase ACE JSON (AceJson).
// Errors are reported on stderr with a structured message and a non-zero exit code.

using System.CommandLine;
using System.Text.Json;
using Ace.Core.Configuration;
using Ace.Core.Engines;
using Ace.Core.Graph;
using Ace.Core.Models;
using Ace.Core.Platform;
using Ace.Core.Services;

// ---------------------------------------------------------------- shared symbols

var pathArgument = new Argument<string>("path")
{
    Description = "Repository root to operate on.",
};

// ---------------------------------------------------------------- init

var initCommand = new Command("init", "Initialize ACE in a repository (.ace directory + starter ace.json).");
var initJson = Cli.CreateJsonOption();
initCommand.Options.Add(initJson);
initCommand.Arguments.Add(pathArgument);
initCommand.SetAction(async (parseResult, cancellationToken) => await Cli.RunAsync(async () =>
{
    var path = parseResult.GetValue(pathArgument)!;
    var root = Cli.RequireDirectory(path);

    var options = new AceOptions();
    var aceDirectory = Path.Combine(root, options.IndexPath);
    var createdDirectory = !Directory.Exists(aceDirectory);
    Directory.CreateDirectory(aceDirectory);

    var configPath = Path.Combine(root, AceOptionsFactory.ConfigFileName);
    var createdConfig = false;
    if (!File.Exists(configPath))
    {
        File.WriteAllText(
            configPath,
            // AceOptionsFactory binds the "ace" section, so wrap the defaults in it.
            JsonSerializer.Serialize(new { ace = new AceOptions() }, Cli.IndentedJson) + Environment.NewLine);
        createdConfig = true;
    }

    if (Cli.UseJson(parseResult, initJson))
    {
        Cli.PrintJson(new
        {
            repositoryPath = root,
            aceDirectory,
            createdDirectory,
            configFile = configPath,
            createdConfig,
        });
    }
    else
    {
        Console.WriteLine("ACE initialized");
        Console.WriteLine($"  repository : {root}");
        Console.WriteLine($"  data dir   : {aceDirectory}{(createdDirectory ? " (created)" : " (already existed)")}");
        Console.WriteLine($"  config     : {configPath}{(createdConfig ? " (created with defaults)" : " (already existed, left untouched)")}");
    }

    return 0;
}, cancellationToken));

// ---------------------------------------------------------------- index

var indexCommand = new Command("index", "Index a repository and build its code graph.");
var indexJson = Cli.CreateJsonOption();
indexCommand.Options.Add(indexJson);
indexCommand.Arguments.Add(pathArgument);
indexCommand.SetAction(async (parseResult, cancellationToken) => await Cli.RunAsync(async () =>
{
    var path = parseResult.GetValue(pathArgument)!;
    Cli.RequireDirectory(path);

    var facade = Cli.CreateFacade();
    var context = await facade.AnalyzeRepositoryAsync(path, cancellationToken);
    var graph = await facade.BuildGraphAsync(path, cancellationToken);

    if (Cli.UseJson(parseResult, indexJson))
    {
        Cli.PrintJson(new
        {
            repositoryPath = context.RepositoryPath,
            fileCount = context.FileCount,
            sourceFileCount = context.SourceFileCount,
            nodeCount = graph.NodeCount,
            edgeCount = graph.EdgeCount,
            durationMs = graph.DurationMs,
            persistedPath = graph.PersistedPath,
        });
    }
    else
    {
        Console.WriteLine("ACE index complete");
        Console.WriteLine($"  repository : {context.RepositoryPath}");
        Cli.PrintKv("files", context.FileCount.ToString());
        Cli.PrintKv("source files", context.SourceFileCount.ToString());
        Cli.PrintKv("nodes", graph.NodeCount.ToString());
        Cli.PrintKv("edges", graph.EdgeCount.ToString());
        Cli.PrintKv("duration", $"{graph.DurationMs} ms");
        Cli.PrintKv("graph", graph.PersistedPath);
    }

    return 0;
}, cancellationToken));

// ---------------------------------------------------------------- status

var statusCommand = new Command("status", "Show ACE engine/index status for a repository.");
var statusJson = Cli.CreateJsonOption();
statusCommand.Options.Add(statusJson);
statusCommand.Arguments.Add(pathArgument);
statusCommand.SetAction(async (parseResult, cancellationToken) => await Cli.RunAsync(async () =>
{
    var path = parseResult.GetValue(pathArgument)!;
    Cli.RequireDirectory(path);

    var facade = Cli.CreateFacade();
    var status = await facade.GetStatusAsync(path, cancellationToken);

    if (Cli.UseJson(parseResult, statusJson))
    {
        Cli.PrintJson(status);
    }
    else
    {
        Console.WriteLine("ACE status");
        Console.WriteLine($"  repository      : {status.RepositoryPath}");
        Cli.PrintKv("api version", status.ApiVersion);
        Cli.PrintKv("indexed", status.Indexed.ToString().ToLowerInvariant());
        Cli.PrintKv("files", status.FileCount.ToString());
        Cli.PrintKv("source files", status.SourceFileCount.ToString());
        Cli.PrintKv("nodes", status.NodeCount.ToString());
        Cli.PrintKv("edges", status.EdgeCount.ToString());
        Cli.PrintKv("index version", status.IndexVersion.ToString());
        Cli.PrintKv("analyzer", $"{status.AnalyzerVersion} (current: {status.CurrentAnalyzerVersion})");
        Cli.PrintKv("stale", status.Stale.ToString().ToLowerInvariant());
        Cli.PrintKv("languages", Cli.Join(status.Languages));
        Cli.PrintKv("test projects", Cli.Join(status.TestProjects));
        if (status.FailedFiles.Count > 0)
        {
            Cli.PrintKv("failed files", status.FailedFiles.Count.ToString());
            foreach (var (file, error) in status.FailedFiles)
            {
                Console.WriteLine($"    - {file}: {error}");
            }
        }
    }

    return 0;
}, cancellationToken));

// ---------------------------------------------------------------- analyze

var analyzeCommand = new Command("analyze", "Analyze a repository and print its structured context.");
var analyzeJson = Cli.CreateJsonOption();
analyzeCommand.Options.Add(analyzeJson);
analyzeCommand.Arguments.Add(pathArgument);
analyzeCommand.SetAction(async (parseResult, cancellationToken) => await Cli.RunAsync(async () =>
{
    var path = parseResult.GetValue(pathArgument)!;
    Cli.RequireDirectory(path);

    var facade = Cli.CreateFacade();
    var context = await facade.AnalyzeRepositoryAsync(path, cancellationToken);

    if (Cli.UseJson(parseResult, analyzeJson))
    {
        Cli.PrintJson(context);
    }
    else
    {
        Console.WriteLine("Repository context");
        Console.WriteLine($"  repository         : {context.RepositoryPath}");
        Cli.PrintKv("files", context.FileCount.ToString());
        Cli.PrintKv("source files", context.SourceFileCount.ToString());
        Cli.PrintKv("projects", context.ProjectCount.ToString());
        Cli.PrintKv("languages", Cli.Join(context.Languages));
        Cli.PrintKv("frameworks", Cli.Join(context.Frameworks));
        Cli.PrintKv("build systems", Cli.Join(context.BuildSystems));
        Cli.PrintKv("test projects", Cli.Join(context.TestProjects));
        Cli.PrintKv("dependency systems", Cli.Join(context.DependencySystems));
    }

    return 0;
}, cancellationToken));

// ---------------------------------------------------------------- impact

var impactFilesArgument = new Argument<string[]>("files")
{
    Description = "Changed files (repository-relative paths); omit to use the git working tree.",
    Arity = ArgumentArity.ZeroOrMore,
};
var diffOption = new Option<string>("--diff")
{
    Description = "Take changed files from 'git diff --name-only <range>' (e.g. HEAD~1..HEAD).",
};

var impactCommand = new Command("impact", "Analyze the impact of changed files.");
var impactJson = Cli.CreateJsonOption();
impactCommand.Options.Add(impactJson);
impactCommand.Arguments.Add(pathArgument);
impactCommand.Arguments.Add(impactFilesArgument);
impactCommand.Options.Add(diffOption);
impactCommand.SetAction(async (parseResult, cancellationToken) => await Cli.RunAsync(async () =>
{
    var path = parseResult.GetValue(pathArgument)!;
    Cli.RequireDirectory(path);

    var files = parseResult.GetValue(impactFilesArgument) ?? [];
    var diff = parseResult.GetValue(diffOption);
    var changedFiles = await Cli.ResolveChangedFilesAsync(path, files, diff, cancellationToken);

    var facade = Cli.CreateFacade();
    var report = await facade.AnalyzeImpactAsync(path, changedFiles, cancellationToken);

    if (Cli.UseJson(parseResult, impactJson))
    {
        Cli.PrintJson(report);
    }
    else
    {
        Console.WriteLine($"Impact analysis — {changedFiles.Count} changed file(s)");
        Console.WriteLine($"  Risk: {report.RiskLevel} (score {report.RiskScore}/100){(report.Truncated ? "  [truncated]" : string.Empty)}");
        Cli.PrintList("Changed components", report.ChangedComponents);
        Cli.PrintList("Direct affected", report.DirectAffectedComponents);
        Cli.PrintList("Indirect affected", report.IndirectAffectedComponents);
        Cli.PrintList("Affected projects", report.AffectedProjects);
        Cli.PrintList("Affected APIs", report.AffectedApis);
        Cli.PrintList("Affected tests", report.AffectedTests);
        if (report.Evidence.Count > 0)
        {
            Console.WriteLine("  Evidence:");
            foreach (var link in report.Evidence)
            {
                Console.WriteLine($"    {link.Source} --{link.Relationship}--> {link.Target}");
            }
        }
    }

    return 0;
}, cancellationToken));

// ---------------------------------------------------------------- graph

var symbolArgument = new Argument<string>("symbol")
{
    Description = "Symbol name or graph node id.",
};
var directionOption = new Option<string>("--direction")
{
    Description = "Traversal direction: both | incoming | outgoing.",
    DefaultValueFactory = _ => "both",
};

var graphQueryCommand = new Command("query", "Query graph neighbors of a symbol (outgoing resolves dependencies).");
var graphJson = Cli.CreateJsonOption();
graphQueryCommand.Options.Add(graphJson);
graphQueryCommand.Arguments.Add(symbolArgument);
graphQueryCommand.Options.Add(directionOption);

var graphCommand = new Command("graph", "Code graph operations.");
graphCommand.Arguments.Add(pathArgument);
graphCommand.Subcommands.Add(graphQueryCommand);
graphQueryCommand.SetAction(async (parseResult, cancellationToken) => await Cli.RunAsync(async () =>
{
    // 'path' is declared on the parent 'graph' command and inherited here.
    var path = parseResult.GetValue(pathArgument)!;
    var symbol = parseResult.GetValue(symbolArgument)!;
    var direction = Cli.ParseDirection(parseResult.GetValue(directionOption)!);
    Cli.RequireDirectory(path);

    var facade = Cli.CreateFacade();
    IReadOnlyList<GraphNode> neighbors;

    if (direction == EdgeDirection.Outgoing)
    {
        // Name-resolved outgoing dependencies (CALLS/REFERENCES/IMPLEMENTS/INHERITS/DEPENDS_ON/USES).
        neighbors = await facade.GetDependenciesAsync(path, symbol, cancellationToken);
    }
    else
    {
        // Try the symbol as an exact node id first, then resolve by name.
        neighbors = await facade.QueryGraphAsync(path, symbol, direction: direction, cancellationToken: cancellationToken);
        if (neighbors.Count == 0)
        {
            var matches = await facade.SearchCodeAsync(path, symbol, cancellationToken);
            var target = matches.FirstOrDefault(node => string.Equals(node.Name, symbol, StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault();
            if (target is not null)
            {
                neighbors = await facade.QueryGraphAsync(path, target.Id, direction: direction, cancellationToken: cancellationToken);
            }
        }
    }

    if (Cli.UseJson(parseResult, graphJson))
    {
        Cli.PrintJson(neighbors);
    }
    else
    {
        var distinct = neighbors.DistinctBy(node => node.Id, StringComparer.Ordinal).ToList();
        Console.WriteLine($"Graph query '{symbol}' — {distinct.Count} neighbor(s) [{direction}]");
        foreach (var node in distinct)
        {
            var location = string.IsNullOrEmpty(node.FilePath) ? string.Empty : $"  ({node.FilePath})";
            Console.WriteLine($"  - [{node.Type}] {node.Id}{location}");
        }
    }

    return 0;
}, cancellationToken));

// ---------------------------------------------------------------- context

var queryArgument = new Argument<string>("query")
{
    Description = "Context query (symbol name, file or topic).",
};
var maxItemsOption = new Option<int>("--max-items")
{
    Description = "Maximum number of context items to return.",
    DefaultValueFactory = _ => ContextEngine.DefaultMaxItems,
};

var contextCommand = new Command("context", "Get prioritized context for a query (7-tier ranking).");
var contextJson = Cli.CreateJsonOption();
contextCommand.Options.Add(contextJson);
contextCommand.Arguments.Add(pathArgument);
contextCommand.Arguments.Add(queryArgument);
contextCommand.Options.Add(maxItemsOption);
contextCommand.SetAction(async (parseResult, cancellationToken) => await Cli.RunAsync(async () =>
{
    var path = parseResult.GetValue(pathArgument)!;
    var query = parseResult.GetValue(queryArgument)!;
    var maxItems = parseResult.GetValue(maxItemsOption);
    Cli.RequireDirectory(path);

    var facade = Cli.CreateFacade();
    var items = await facade.GetContextAsync(path, query, maxItems, cancellationToken);

    if (Cli.UseJson(parseResult, contextJson))
    {
        Cli.PrintJson(items);
    }
    else
    {
        Console.WriteLine($"Context for '{query}' — {items.Count} item(s)");
        foreach (var item in items)
        {
            var where = string.IsNullOrEmpty(item.Path) ? string.Empty : $"  [{item.Path}]";
            Console.WriteLine($"  T{item.Tier} {item.Title}{where}");
            if (!string.IsNullOrEmpty(item.Reason))
            {
                Console.WriteLine($"      {item.Reason}");
            }
        }
    }

    return 0;
}, cancellationToken));

// ---------------------------------------------------------------- tests

var testsFilesArgument = new Argument<string[]>("files")
{
    Description = "Changed files (repository-relative paths).",
    Arity = ArgumentArity.OneOrMore,
};

var testsCommand = new Command("tests", "List tests affected by changed files.");
var testsJson = Cli.CreateJsonOption();
testsCommand.Options.Add(testsJson);
testsCommand.Arguments.Add(pathArgument);
testsCommand.Arguments.Add(testsFilesArgument);
testsCommand.SetAction(async (parseResult, cancellationToken) => await Cli.RunAsync(async () =>
{
    var path = parseResult.GetValue(pathArgument)!;
    var files = parseResult.GetValue(testsFilesArgument)!;
    Cli.RequireDirectory(path);

    var facade = Cli.CreateFacade();
    var report = await facade.GetAffectedTestsAsync(path, files, cancellationToken);

    if (Cli.UseJson(parseResult, testsJson))
    {
        Cli.PrintJson(report);
    }
    else
    {
        Console.WriteLine($"Affected tests — {report.AffectedTests.Count} test(s) affected by {report.ChangedFiles.Count} changed file(s)");
        foreach (var test in report.AffectedTests)
        {
            var where = string.IsNullOrEmpty(test.FilePath) ? string.Empty : $"  [{test.FilePath}]";
            Console.WriteLine($"  - {test.Name}{where}");
            if (!string.IsNullOrEmpty(test.Reason))
            {
                Console.WriteLine($"      {test.Reason}");
            }
        }
    }

    return 0;
}, cancellationToken));

// ---------------------------------------------------------------- regression

var regressionFilesOption = new Option<string[]>("--files")
{
    Description = "Changed files (repository-relative paths); omit to use the git working tree.",
    Arity = ArgumentArity.OneOrMore,
};

var regressionCommand = new Command("regression", "Recommend regression scope for a change set.");
var regressionJson = Cli.CreateJsonOption();
regressionCommand.Options.Add(regressionJson);
regressionCommand.Arguments.Add(pathArgument);
regressionCommand.Options.Add(regressionFilesOption);
regressionCommand.Options.Add(diffOption);
regressionCommand.SetAction(async (parseResult, cancellationToken) => await Cli.RunAsync(async () =>
{
    var path = parseResult.GetValue(pathArgument)!;
    Cli.RequireDirectory(path);

    var files = parseResult.GetValue(regressionFilesOption) ?? [];
    var diff = parseResult.GetValue(diffOption);
    var changedFiles = await Cli.ResolveChangedFilesAsync(path, files, diff, cancellationToken);

    var facade = Cli.CreateFacade();
    var scope = await facade.GetRegressionScopeAsync(path, changedFiles, cancellationToken);

    if (Cli.UseJson(parseResult, regressionJson))
    {
        Cli.PrintJson(scope);
    }
    else
    {
        Console.WriteLine($"Regression scope — {scope.ChangedFiles.Count} changed file(s)");
        Console.WriteLine($"  Risk       : {scope.RiskLevel}");
        Console.WriteLine($"  Recommended: {scope.RecommendedScope}");
        Cli.PrintList("Potential impact", scope.PotentialImpact);
        if (scope.AffectedTests.Count > 0)
        {
            Console.WriteLine("  Affected tests:");
            foreach (var test in scope.AffectedTests)
            {
                Console.WriteLine($"    - {test.Name}");
            }
        }

        foreach (var note in scope.Notes)
        {
            Console.WriteLine($"  note: {note}");
        }
    }

    return 0;
}, cancellationToken));

// ---------------------------------------------------------------- root

var rootCommand = new RootCommand("ace — ACE Agent Context Engine CLI (same intelligence as the ACE MCP server).");
rootCommand.Subcommands.Add(initCommand);
rootCommand.Subcommands.Add(indexCommand);
rootCommand.Subcommands.Add(statusCommand);
rootCommand.Subcommands.Add(analyzeCommand);
rootCommand.Subcommands.Add(impactCommand);
rootCommand.Subcommands.Add(graphCommand);
rootCommand.Subcommands.Add(contextCommand);
rootCommand.Subcommands.Add(testsCommand);
rootCommand.Subcommands.Add(regressionCommand);

return await rootCommand.Parse(args).InvokeAsync();

// ---------------------------------------------------------------- helpers

/// <summary>User-facing error with a clean message (no stack trace).</summary>
internal sealed class CliException(string message) : Exception(message);

/// <summary>Shared CLI plumbing: services, error handling and output helpers.</summary>
internal static class Cli
{
    /// <summary>AceJson conventions (camelCase, enums as strings) with indentation for humans.</summary>
    public static readonly JsonSerializerOptions IndentedJson = new(AceJson.Options) { WriteIndented = true };

    /// <summary>Per-command --json option (System.CommandLine 2.x does not inherit parent options into subcommand parsing).</summary>
    public static Option<bool> CreateJsonOption() => new("--json")
    {
        Description = "Print the raw camelCase ACE JSON instead of the human-readable report.",
    };

    /// <summary>Same seam the MCP server uses (SRS §20: CLI shares ACE Core services).</summary>
    public static AceEngineFacade CreateFacade() => new(new FileSystemService());

    /// <summary>Runs a command body, converting exceptions into a clean error + exit code 1.</summary>
    public static async Task<int> RunAsync(Func<Task<int>> body, CancellationToken cancellationToken)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("ace: interrupted.");
            return 1;
        }
        catch (CliException ex)
        {
            Console.Error.WriteLine($"ace error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ace error: {ex.Message}");
            return 1;
        }
    }

    public static bool UseJson(ParseResult parseResult, Option<bool> jsonOption)
        => parseResult.GetValue(jsonOption);

    public static string RequireDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
        {
            throw new CliException($"Repository path does not exist: {root}");
        }

        return root;
    }

    /// <summary>
    /// Changed-file resolution shared by 'impact' and 'regression' (FR-007):
    /// --diff <range> wins; then explicit files; then the git working tree.
    /// Git-based resolution is gated on ace.enableGitAnalysis (SR: configuration);
    /// when disabled, ACE refuses to shell out to git with a clear message.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ResolveChangedFilesAsync(
        string path,
        IReadOnlyList<string> explicitFiles,
        string? diffRange,
        CancellationToken cancellationToken)
    {
        var useWorkingTree = string.IsNullOrWhiteSpace(diffRange) && explicitFiles.Count == 0;
        try
        {
            return await CreateFacade().ResolveChangedFilesAsync(
                path, explicitFiles, useWorkingTree, diffRange, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new CliException(ex.Message);
        }
    }

    public static EdgeDirection ParseDirection(string direction) => direction.ToLowerInvariant() switch
    {
        "both" => EdgeDirection.Both,
        "incoming" or "in" => EdgeDirection.Incoming,
        "outgoing" or "out" => EdgeDirection.Outgoing,
        _ => throw new CliException($"Invalid --direction '{direction}'. Use both, incoming or outgoing."),
    };

    public static void PrintJson(object model)
        => Console.WriteLine(JsonSerializer.Serialize(model, IndentedJson));

    public static void PrintKv(string label, string value)
        => Console.WriteLine($"  {label,-16}: {value}");

    public static void PrintList(string label, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        Console.WriteLine($"  {label} ({items.Count}):");
        foreach (var item in items)
        {
            Console.WriteLine($"    - {item}");
        }
    }

    public static string Join(IReadOnlyList<string> values)
        => values.Count == 0 ? "-" : string.Join(", ", values);
}
