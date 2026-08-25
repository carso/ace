namespace Ace.Core.Parsing;

/// <summary>Syntactic kind of a declared type.</summary>
public enum TypeKind
{
    Class,
    Interface,
    Record,
    Struct,
    Enum,
    Delegate,
}

/// <summary>A declared type (class/interface/record/struct/enum) extracted from a source file.</summary>
public sealed record TypeInfo
{
    public required string Name { get; init; }

    public required TypeKind Kind { get; init; }

    /// <summary>Declaring namespace, empty for the global namespace.</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>Names from the base list (base classes and interfaces, unresolved).</summary>
    public IReadOnlyList<string> BaseTypes { get; init; } = [];

    /// <summary>Attribute names applied to the type (e.g. "ApiController", "Fact").</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>1-based start line of the declaration.</summary>
    public int StartLine { get; init; }

    /// <summary>1-based end line of the declaration.</summary>
    public int EndLine { get; init; }

    public bool IsPublic { get; init; }

    public IReadOnlyList<MemberInfo> Members { get; init; } = [];
}

/// <summary>Syntactic kind of a declared member.</summary>
public enum MemberKind
{
    Method,
    Property,
    Field,
    Constructor,
    Event,
    Delegate,
}

/// <summary>A declared member (method/property/field/constructor) extracted from a type.</summary>
public sealed record MemberInfo
{
    public required string Name { get; init; }

    public required MemberKind Kind { get; init; }

    /// <summary>Declared signature text, e.g. "decimal CalculateDiscount(Customer customer)".</summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>Declared return type text for methods, declared type text for fields/properties.</summary>
    public string? ReturnType { get; init; }

    /// <summary>Declared type names of parameters (for methods/constructors).</summary>
    public IReadOnlyList<string> ParameterTypes { get; init; } = [];

    /// <summary>Attribute names applied to the member (e.g. "Fact", "HttpGet").</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    public int StartLine { get; init; }

    public bool IsPublic { get; init; }
}

/// <summary>An unresolved method-call site.</summary>
public sealed record InvocationSite
{
    /// <summary>Callee identifier (right-most name of the invocation expression).</summary>
    public required string CalleeName { get; init; }

    /// <summary>Name of the type containing the call site.</summary>
    public string ContainingType { get; init; } = string.Empty;

    /// <summary>Name of the member containing the call site, empty when at type level.</summary>
    public string ContainingMember { get; init; } = string.Empty;

    public required string File { get; init; }

    /// <summary>1-based line of the call site.</summary>
    public int Line { get; init; }
}

/// <summary>An object-creation site (<c>new X(...)</c>).</summary>
public sealed record CreationSite
{
    /// <summary>Simple name of the type being created (generic arguments stripped).</summary>
    public required string TypeName { get; init; }

    public string ContainingType { get; init; } = string.Empty;

    public string ContainingMember { get; init; } = string.Empty;

    public required string File { get; init; }

    public int Line { get; init; }
}

/// <summary>A detected dependency-injection registration (AddSingleton/AddScoped/AddTransient).</summary>
public sealed record DiRegistration
{
    /// <summary>Registration method name, e.g. "AddScoped".</summary>
    public required string MethodName { get; init; }

    /// <summary>Simple names of generic type arguments, e.g. ["ICustomerRepository", "InMemoryCustomerRepository"].</summary>
    public IReadOnlyList<string> TypeArguments { get; init; } = [];

    public string ContainingType { get; init; } = string.Empty;

    public string ContainingMember { get; init; } = string.Empty;

    public required string File { get; init; }

    public int Line { get; init; }
}

/// <summary>A parse diagnostic (error or warning) recovered from the syntax tree.</summary>
/// <param name="Id">Diagnostic identifier, e.g. "CS1002".</param>
/// <param name="Message">Human-readable message.</param>
/// <param name="Line">1-based line where the diagnostic was reported.</param>
/// <param name="Severity">"error" or "warning".</param>
public sealed record ParseDiagnostic(string Id, string Message, int Line, string Severity);

/// <summary>
/// Everything a language analyzer extracted from a single source file (FR-003).
/// Syntax-only: no semantic resolution, names are as written in source.
/// </summary>
public sealed record FileAnalysis
{
    /// <summary>Repository-relative path of the analyzed file (forward slashes).</summary>
    public required string FilePath { get; init; }

    /// <summary>Language that produced this analysis (e.g. "C#").</summary>
    public required string Language { get; init; }

    /// <summary>All namespaces declared in the file (file-scoped or block).</summary>
    public IReadOnlyList<string> Namespaces { get; init; } = [];

    public IReadOnlyList<TypeInfo> Types { get; init; } = [];

    /// <summary>Using directives, e.g. "Customer.Domain".</summary>
    public IReadOnlyList<string> Usings { get; init; } = [];

    public IReadOnlyList<InvocationSite> Invocations { get; init; } = [];

    public IReadOnlyList<CreationSite> Creations { get; init; } = [];

    public IReadOnlyList<DiRegistration> DiRegistrations { get; init; } = [];

    /// <summary>Parse diagnostics; non-empty means the file was malformed but partially analyzed (SRS §17).</summary>
    public IReadOnlyList<ParseDiagnostic> Diagnostics { get; init; } = [];

    public bool HasDiagnostics => Diagnostics.Count > 0;
}

/// <summary>Analysis input + result pair handed to the graph builder.</summary>
/// <param name="RelativePath">Repository-relative path (forward slashes).</param>
/// <param name="Analysis">Analyzer output for that file.</param>
public sealed record AnalyzedFile(string RelativePath, FileAnalysis Analysis);
