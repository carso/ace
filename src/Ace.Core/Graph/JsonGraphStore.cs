using System.Text.Json;
using Ace.Core.Indexing;
using Ace.Core.Models;
using Ace.Core.Platform;

namespace Ace.Core.Graph;

/// <summary>
/// JSON persistence for the code graph at <c>&lt;repo&gt;/.ace/graph.json</c>
/// (camelCase per §9). Writes go through a temp file + rename so a failed write
/// preserves the previous valid graph (SRS §17).
/// </summary>
public sealed class JsonGraphStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private readonly IFileSystemService _fileSystem;

    public JsonGraphStore(IFileSystemService fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    /// <summary>Path of the graph file for a repository root.</summary>
    public static string GetGraphPath(string repositoryRoot, string indexDirectory = ".ace")
        => Path.Combine(repositoryRoot, indexDirectory, "graph.json");

    /// <summary>Loads the persisted graph, or null when absent or corrupt (SRS §17).</summary>
    public ICodeGraph? Load(string repositoryRoot, string indexDirectory = ".ace")
    {
        var path = GetGraphPath(repositoryRoot, indexDirectory);
        if (!_fileSystem.FileExists(path))
        {
            return null;
        }

        try
        {
            var json = _fileSystem.ReadAllText(path);
            var document = JsonSerializer.Deserialize<GraphDocument>(json, SerializerOptions);
            if (document is null)
            {
                return null;
            }

            // A graph stamped for a different repository root is treated as absent →
            // rebuild (SRS §17). Legacy graphs without a stamp are still accepted.
            if (!string.IsNullOrEmpty(document.Repository) &&
                !RepositoryIndex.IsSamePath(document.Repository, repositoryRoot))
            {
                return null;
            }

            var graph = new InMemoryCodeGraph();
            foreach (var node in document.Nodes ?? [])
            {
                graph.AddNode(node);
            }

            foreach (var edge in document.Edges ?? [])
            {
                graph.AddEdge(edge);
            }

            return graph;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Persists the graph atomically (temp file + rename).</summary>
    public void Save(ICodeGraph graph, string repositoryRoot, string indexDirectory = ".ace")
    {
        ArgumentNullException.ThrowIfNull(graph);

        var destination = GetGraphPath(repositoryRoot, indexDirectory);
        _fileSystem.CreateDirectory(Path.GetDirectoryName(destination)!);

        var document = new GraphDocument
        {
            Repository = repositoryRoot,
            Nodes = graph.GetNodes().OrderBy(n => n.Id, StringComparer.Ordinal).ToList(),
            Edges = graph.GetEdges().ToList(),
        };

        var tempPath = destination + ".tmp." + Guid.NewGuid().ToString("N")[..8];
        try
        {
            var json = JsonSerializer.Serialize(document, SerializerOptions);
            _fileSystem.WriteAllText(tempPath, json);
            _fileSystem.ReplaceFile(tempPath, destination);
        }
        finally
        {
            if (_fileSystem.FileExists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort only; a leftover temp file is harmless.
                }
            }
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        // AceJson conventions (camelCase, enums as strings, nulls omitted) with
        // indentation for human inspection of .ace artifacts.
        var options = new JsonSerializerOptions(AceJson.Options)
        {
            WriteIndented = true,
        };

        return options;
    }

    private sealed class GraphDocument
    {
        /// <summary>Repository root the graph was built for (validated on load).</summary>
        public string? Repository { get; set; }

        public List<GraphNode>? Nodes { get; set; }

        public List<GraphEdge>? Edges { get; set; }
    }
}
