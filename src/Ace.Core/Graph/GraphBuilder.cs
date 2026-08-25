using Ace.Core.Models;
using Ace.Core.Parsing;

namespace Ace.Core.Graph;

/// <summary>
/// Builds the ACE code graph from analyzer output plus csproj metadata (FR-004/FR-005).
/// Observed facts (CONTAINS, DEPENDS_ON) carry <see cref="Confidence.Observed"/>;
/// deterministic derivations (INHERITS, IMPLEMENTS, REFERENCES) carry
/// <see cref="Confidence.Calculated"/>; name-heuristic edges (CALLS, TESTS) carry
/// <see cref="Confidence.Inferred"/> with a score below 1.0 and file:line evidence (§4.3).
/// </summary>
public sealed class GraphBuilder
{
    /// <summary>Analyzer label recorded on edges produced from C# syntax analysis.</summary>
    public const string AnalyzerId = "csharp-roslyn/1.0";

    /// <summary>Confidence score for name-heuristic CALLS edges (§4.3).</summary>
    public const double CallConfidenceScore = 0.6;

    /// <summary>Confidence score for TESTS edges derived from the naming convention.</summary>
    public const double TestNamingConfidenceScore = 0.8;

    private const string UnassignedProject = "_repo";
    private const string GlobalNamespace = "<global>";

    private readonly InMemoryCodeGraph _graph = new();
    private readonly HashSet<string> _nodeIds = new(StringComparer.Ordinal);
    private readonly HashSet<(string Source, string Target, EdgeType Type)> _edgeKeys = new();

    // project name → node id
    private readonly Dictionary<string, string> _projectIds = new(StringComparer.OrdinalIgnoreCase);
    // simple type name → type node ids (deterministic insertion order)
    private readonly Dictionary<string, List<string>> _typesByName = new(StringComparer.Ordinal);
    // "Namespace.Type" → type node id
    private readonly Dictionary<string, string> _typesByQualifiedName = new(StringComparer.Ordinal);
    // type node id → TypeKind
    private readonly Dictionary<string, TypeKind> _typeKinds = new(StringComparer.Ordinal);
    // type node id → declaring namespace
    private readonly Dictionary<string, string> _typeNamespaces = new(StringComparer.Ordinal);
    // "file|typeName" → type node id
    private readonly Dictionary<string, string> _typesByFile = new(StringComparer.Ordinal);
    // method name → method node ids
    private readonly Dictionary<string, List<string>> _methodsByName = new(StringComparer.Ordinal);
    // member node ids (methods + ctors)
    private readonly HashSet<string> _memberIds = new(StringComparer.Ordinal);
    // test type node ids
    private readonly List<string> _testTypeIds = [];

    private List<CsprojInfo> _projects = [];

    /// <summary>Builds the graph for a repository's analyzed files and project files.</summary>
    public ICodeGraph Build(IReadOnlyCollection<AnalyzedFile> analyzedFiles, IReadOnlyCollection<CsprojInfo> projectFiles)
    {
        ArgumentNullException.ThrowIfNull(analyzedFiles);
        ArgumentNullException.ThrowIfNull(projectFiles);

        _projects = projectFiles
            .OrderByDescending(p => p.RelativeDirectory.Length)
            .ThenBy(p => p.RelativePath, StringComparer.Ordinal)
            .ToList();

        foreach (var project in _projects)
        {
            AddProjectNode(project.ProjectName, project.RelativePath);
        }

        foreach (var file in analyzedFiles.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            AddTypesForFile(file);
        }

        AddMemberNodes(analyzedFiles);
        AddInheritanceEdges(analyzedFiles);
        AddReferenceEdges(analyzedFiles);
        AddCallEdges(analyzedFiles);
        AddProjectDependencyEdges();
        AddTestEdges(analyzedFiles);

        return _graph;
    }

    // ---------------------------------------------------------------- nodes

    private void AddProjectNode(string projectName, string? filePath = null)
    {
        var id = projectName;
        if (!_nodeIds.Add(id))
        {
            return;
        }

        _projectIds[projectName] = id;
        _graph.AddNode(new GraphNode
        {
            Id = id,
            Type = NodeType.Project,
            Name = projectName,
            FilePath = filePath,
            Project = projectName,
        });
    }

    private string OwningProject(string relativePath)
    {
        foreach (var project in _projects)
        {
            if (project.RelativeDirectory.Length > 0 &&
                relativePath.StartsWith(project.RelativeDirectory + "/", StringComparison.OrdinalIgnoreCase))
            {
                return project.ProjectName;
            }
        }

        return UnassignedProject;
    }

    private void AddTypesForFile(AnalyzedFile file)
    {
        var project = OwningProject(file.RelativePath);
        AddProjectNode(project, null);
        var projectId = _projectIds[project];

        foreach (var type in file.Analysis.Types)
        {
            var namespaceName = type.Namespace.Length > 0 ? type.Namespace : GlobalNamespace;
            var namespaceId = $"{projectId}:{namespaceName}";
            if (_nodeIds.Add(namespaceId))
            {
                _graph.AddNode(new GraphNode
                {
                    Id = namespaceId,
                    Type = NodeType.Namespace,
                    Name = type.Namespace.Length > 0 ? type.Namespace : GlobalNamespace,
                    Project = project,
                    FilePath = file.RelativePath,
                });

                AddEdge(projectId, namespaceId, EdgeType.Contains, Confidence.Observed, "namespace-declaration", file.RelativePath);
            }

            var typeId = $"{projectId}:{(type.Namespace.Length > 0 ? type.Namespace + "." : string.Empty)}{type.Name}";
            var isTestType = IsTestType(type);
            var nodeType = isTestType ? NodeType.Test : MapNodeType(type.Kind);

            if (_nodeIds.Add(typeId))
            {
                _graph.AddNode(new GraphNode
                {
                    Id = typeId,
                    Type = nodeType,
                    Name = type.Name,
                    FilePath = file.RelativePath,
                    Project = project,
                    Namespace = type.Namespace,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["kind"] = type.Kind.ToString(),
                        ["isPublic"] = type.IsPublic,
                        ["attributes"] = type.Attributes,
                        ["startLine"] = type.StartLine,
                        ["endLine"] = type.EndLine,
                    },
                });

                if (!_typesByName.TryGetValue(type.Name, out var namesList))
                {
                    namesList = [];
                    _typesByName[type.Name] = namesList;
                }

                namesList.Add(typeId);
                _typesByQualifiedName.TryAdd($"{(type.Namespace.Length > 0 ? type.Namespace + "." : string.Empty)}{type.Name}", typeId);
                _typeKinds[typeId] = type.Kind;
                _typeNamespaces[typeId] = type.Namespace;
                if (isTestType)
                {
                    _testTypeIds.Add(typeId);
                }
            }

            _typesByFile.TryAdd($"{file.RelativePath}|{type.Name}", typeId);
            AddEdge(namespaceId, typeId, EdgeType.Contains, Confidence.Observed, "type-declaration", $"{file.RelativePath}:{type.StartLine}");
        }
    }

    private void AddMemberNodes(IReadOnlyCollection<AnalyzedFile> analyzedFiles)
    {
        foreach (var file in analyzedFiles.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            foreach (var type in file.Analysis.Types)
            {
                if (!_typesByFile.TryGetValue($"{file.RelativePath}|{type.Name}", out var typeId))
                {
                    continue;
                }

                foreach (var member in type.Members)
                {
                    // Graph keeps at least public methods/constructors; properties and
                    // fields stay in node metadata to bound graph size.
                    var isMethodLike = member.Kind is MemberKind.Method or MemberKind.Constructor;
                    if (!isMethodLike || !member.IsPublic)
                    {
                        continue;
                    }

                    var baseId = $"{typeId}#{member.Name}";
                    var memberId = baseId;
                    var suffix = 2;
                    while (!_memberIds.Add(memberId))
                    {
                        memberId = $"{baseId}#{suffix++}";
                    }

                    _graph.AddNode(new GraphNode
                    {
                        Id = memberId,
                        Type = NodeType.Method,
                        Name = member.Name,
                        FilePath = file.RelativePath,
                        Project = _graph.GetNode(typeId).Project,
                        Namespace = type.Namespace,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["signature"] = member.Signature,
                            ["memberKind"] = member.Kind.ToString(),
                            ["isPublic"] = member.IsPublic,
                            ["startLine"] = member.StartLine,
                            ["attributes"] = member.Attributes,
                        },
                    });

                    if (!_methodsByName.TryGetValue(member.Name, out var methodsList))
                    {
                        methodsList = [];
                        _methodsByName[member.Name] = methodsList;
                    }

                    methodsList.Add(memberId);
                    AddEdge(typeId, memberId, EdgeType.Contains, Confidence.Observed, "member-declaration", $"{file.RelativePath}:{member.StartLine}");
                }
            }
        }
    }

    // ---------------------------------------------------------------- edges

    private void AddInheritanceEdges(IReadOnlyCollection<AnalyzedFile> analyzedFiles)
    {
        foreach (var file in analyzedFiles)
        {
            foreach (var type in file.Analysis.Types)
            {
                if (!_typesByFile.TryGetValue($"{file.RelativePath}|{type.Name}", out var typeId))
                {
                    continue;
                }

                foreach (var baseName in type.BaseTypes)
                {
                    var targetId = ResolveType(baseName, type.Namespace);
                    if (targetId is null || targetId == typeId)
                    {
                        continue;
                    }

                    var edgeType = _typeKinds.GetValueOrDefault(targetId) == TypeKind.Interface
                        ? EdgeType.Implements
                        : EdgeType.Inherits;

                    AddEdge(
                        typeId,
                        targetId,
                        edgeType,
                        Confidence.Calculated,
                        "base-list",
                        $"{file.RelativePath}:{type.StartLine}");
                }
            }
        }
    }

    private void AddReferenceEdges(IReadOnlyCollection<AnalyzedFile> analyzedFiles)
    {
        foreach (var file in analyzedFiles)
        {
            foreach (var type in file.Analysis.Types)
            {
                if (!_typesByFile.TryGetValue($"{file.RelativePath}|{type.Name}", out var typeId))
                {
                    continue;
                }

                foreach (var member in type.Members)
                {
                    var (typeText, evidence) = member.Kind switch
                    {
                        MemberKind.Field => (member.ReturnType, "field-type"),
                        MemberKind.Property => (member.ReturnType, "property-type"),
                        MemberKind.Method or MemberKind.Constructor => (null, null),
                        _ => (null, null),
                    };

                    if (typeText is not null)
                    {
                        AddReferenceEdge(typeId, typeText, type.Namespace, evidence!, $"{file.RelativePath}:{member.StartLine}");
                    }

                    if (member.Kind is MemberKind.Method or MemberKind.Constructor)
                    {
                        foreach (var parameterType in member.ParameterTypes)
                        {
                            AddReferenceEdge(typeId, parameterType, type.Namespace, "parameter-type", $"{file.RelativePath}:{member.StartLine}");
                        }
                    }
                }
            }
        }
    }

    private void AddReferenceEdge(string sourceTypeId, string typeText, string preferredNamespace, string evidence, string location)
    {
        var simpleName = SimplifyTypeName(typeText);
        if (simpleName.Length == 0)
        {
            return;
        }

        var targetId = ResolveType(simpleName, preferredNamespace);
        if (targetId is null || targetId == sourceTypeId)
        {
            return;
        }

        AddEdge(sourceTypeId, targetId, EdgeType.References, Confidence.Calculated, evidence, location);
    }

    private void AddCallEdges(IReadOnlyCollection<AnalyzedFile> analyzedFiles)
    {
        foreach (var file in analyzedFiles)
        {
            foreach (var invocation in file.Analysis.Invocations)
            {
                var sourceId = ResolveSourceNode(file.RelativePath, invocation.ContainingType, invocation.ContainingMember);
                if (sourceId is null)
                {
                    continue;
                }

                var sourceTypeId = TypeIdFor(file.RelativePath, invocation.ContainingType);
                var targetId = ResolveMethodTarget(invocation.CalleeName, sourceTypeId);
                targetId ??= ResolveType(invocation.CalleeName, NamespaceOfType(sourceTypeId));

                if (targetId is not null && targetId != sourceId)
                {
                    AddEdge(sourceId, targetId, EdgeType.Calls, Confidence.Inferred, "invocation", $"{file.RelativePath}:{invocation.Line}", CallConfidenceScore);
                }
            }

            foreach (var creation in file.Analysis.Creations)
            {
                var sourceId = ResolveSourceNode(file.RelativePath, creation.ContainingType, creation.ContainingMember);
                if (sourceId is null)
                {
                    continue;
                }

                var preferredNamespace = NamespaceOfType(TypeIdFor(file.RelativePath, creation.ContainingType));
                var targetId = ResolveType(creation.TypeName, preferredNamespace);
                if (targetId is not null && targetId != sourceId)
                {
                    AddEdge(sourceId, targetId, EdgeType.Calls, Confidence.Inferred, "object-creation", $"{file.RelativePath}:{creation.Line}", CallConfidenceScore);
                }
            }
        }
    }

    private void AddProjectDependencyEdges()
    {
        foreach (var project in _projects)
        {
            if (!_projectIds.TryGetValue(project.ProjectName, out var projectId))
            {
                continue;
            }

            foreach (var projectReference in project.ProjectReferences)
            {
                if (_projectIds.TryGetValue(projectReference.ProjectName, out var targetProjectId))
                {
                    AddEdge(projectId, targetProjectId, EdgeType.DependsOn, Confidence.Observed, "project-reference", project.RelativePath);
                }
            }

            foreach (var packageReference in project.PackageReferences)
            {
                var packageId = $"package:{packageReference.Include}";
                if (_nodeIds.Add(packageId))
                {
                    _graph.AddNode(new GraphNode
                    {
                        Id = packageId,
                        Type = NodeType.Package,
                        Name = packageReference.Include,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["version"] = packageReference.Version,
                        },
                    });
                }

                AddEdge(projectId, packageId, EdgeType.DependsOn, Confidence.Observed, "package-reference", project.RelativePath);
            }
        }
    }

    private void AddTestEdges(IReadOnlyCollection<AnalyzedFile> analyzedFiles)
    {
        foreach (var file in analyzedFiles)
        {
            foreach (var type in file.Analysis.Types)
            {
                if (!IsTestType(type) ||
                    !_typesByFile.TryGetValue($"{file.RelativePath}|{type.Name}", out var testTypeId))
                {
                    continue;
                }

                var targetName = StripTestSuffix(type.Name);
                if (targetName is null || !_typesByName.TryGetValue(targetName, out var candidates))
                {
                    continue;
                }

                foreach (var candidateId in candidates)
                {
                    if (_testTypeIds.Contains(candidateId))
                    {
                        continue;
                    }

                    AddEdge(
                        testTypeId,
                        candidateId,
                        EdgeType.Tests,
                        Confidence.Inferred,
                        "naming-convention",
                        $"{file.RelativePath}:{type.StartLine}",
                        TestNamingConfidenceScore);
                }
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private string? ResolveSourceNode(string file, string containingType, string containingMember)
    {
        var typeId = TypeIdFor(file, containingType);
        if (typeId is null)
        {
            return null;
        }

        if (containingMember.Length > 0)
        {
            var memberId = $"{typeId}#{containingMember}";
            if (_memberIds.Contains(memberId))
            {
                return memberId;
            }
        }

        return typeId;
    }

    private string? TypeIdFor(string file, string typeName)
        => typeName.Length > 0 && _typesByFile.TryGetValue($"{file}|{typeName}", out var id) ? id : null;

    private string NamespaceOfType(string? typeId)
        => typeId is not null && _typeNamespaces.TryGetValue(typeId, out var ns) ? ns : string.Empty;

    private string? ResolveMethodTarget(string calleeName, string? callerTypeId)
    {
        if (!_methodsByName.TryGetValue(calleeName, out var candidates) || candidates.Count == 0)
        {
            return null;
        }

        // Prefer methods declared on other types; a call site naming its own method is
        // usually a recursive/self call which is uninformative for impact analysis.
        var external = candidates.Where(c => !c.StartsWith($"{callerTypeId}#", StringComparison.Ordinal)).ToList();
        var pool = external.Count > 0 ? external : candidates;
        return pool[0];
    }

    private string? ResolveType(string simpleName, string preferredNamespace)
    {
        if (simpleName.Length == 0)
        {
            return null;
        }

        if (preferredNamespace.Length > 0 &&
            _typesByQualifiedName.TryGetValue($"{preferredNamespace}.{simpleName}", out var sameNamespace))
        {
            return sameNamespace;
        }

        return _typesByName.TryGetValue(simpleName, out var ids) && ids.Count > 0 ? ids[0] : null;
    }

    private void AddEdge(
        string sourceId,
        string targetId,
        EdgeType type,
        Confidence confidence,
        string evidence,
        string? location,
        double? confidenceScore = null)
    {
        if (sourceId == targetId || !_edgeKeys.Add((sourceId, targetId, type)))
        {
            return;
        }

        _graph.AddEdge(new GraphEdge
        {
            SourceId = sourceId,
            TargetId = targetId,
            Type = type,
            Confidence = confidence,
            ConfidenceScore = confidenceScore ?? (confidence == Confidence.Inferred ? CallConfidenceScore : 1.0),
            Evidence = evidence,
            Location = location,
        });
    }

    private static bool IsTestType(TypeInfo type)
    {
        if (type.Kind != TypeKind.Class)
        {
            return false;
        }

        if (StripTestSuffix(type.Name) is not null)
        {
            return true;
        }

        return type.Attributes.Any(a =>
            a is "Fact" or "Theory" or "TestMethod" or "Test" or "TestClass" or "TestFixture");
    }

    private static string? StripTestSuffix(string typeName)
    {
        if (typeName.EndsWith("Tests", StringComparison.Ordinal) && typeName.Length > "Tests".Length)
        {
            return typeName[..^"Tests".Length];
        }

        if (typeName.EndsWith("Test", StringComparison.Ordinal) && typeName.Length > "Test".Length)
        {
            return typeName[..^"Test".Length];
        }

        return null;
    }

    private static NodeType MapNodeType(TypeKind kind) => kind switch
    {
        TypeKind.Interface => NodeType.Interface,
        TypeKind.Record => NodeType.Record,
        _ => NodeType.Class,
    };

    /// <summary>Reduces a declared type text to a simple type name ("Customer?[]" → "Customer").</summary>
    private static string SimplifyTypeName(string? typeText)
    {
        if (string.IsNullOrWhiteSpace(typeText))
        {
            return string.Empty;
        }

        var text = typeText.Trim();
        if (text.StartsWith('('))
        {
            // Tuple types are not graph nodes.
            return string.Empty;
        }

        var cut = text.IndexOfAny(['<', '[', '?', '*']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        var dot = text.LastIndexOf('.');
        if (dot >= 0)
        {
            text = text[(dot + 1)..];
        }

        return text.Trim();
    }
}
