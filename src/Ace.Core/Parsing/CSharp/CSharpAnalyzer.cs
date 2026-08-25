using Ace.Core.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ace.Core.Parsing.CSharp;

/// <summary>
/// Syntax-only C# analyzer (FR-003, SRS §22/§25). Uses <see cref="CSharpSyntaxTree.ParseText"/>
/// plus a single <see cref="CSharpSyntaxWalker"/>. Strictly forbidden and unused:
/// CSharpCompilation, SemanticModel, MSBuild/Workspaces. Never throws for malformed
/// input; parse problems are returned as diagnostics (SRS §17).
/// </summary>
public sealed class CSharpAnalyzer : IAnalyzer
{
    public const string LanguageName = "C#";

    public const string AnalyzerVersion = "1.0";

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
    };

    public string Language => LanguageName;

    public string Version => AnalyzerVersion;

    public bool CanHandle(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return SupportedExtensions.Contains(Path.GetExtension(path));
    }

    public Task<FileAnalysis> AnalyzeAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        FileAnalysis analysis;
        try
        {
            analysis = Analyze(path, content ?? string.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Absolute last resort: analysis must never throw (SRS §17).
            analysis = new FileAnalysis
            {
                FilePath = path,
                Language = LanguageName,
                Diagnostics =
                [
                    new ParseDiagnostic("ACE0001", $"Unexpected analyzer failure: {ex.Message}", 1, "error"),
                ],
            };
        }

        return Task.FromResult(analysis);
    }

    private FileAnalysis Analyze(string path, string content)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var diagnostics = tree.GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Select(d => new ParseDiagnostic(
                d.Id,
                d.GetMessage(),
                d.Location.GetLineSpan().StartLinePosition.Line + 1,
                d.Severity == DiagnosticSeverity.Error ? "error" : "warning"))
            .ToList();

        var root = tree.GetRoot();
        var walker = new ExtractionWalker(tree, path);
        walker.Visit(root);

        return new FileAnalysis
        {
            FilePath = path,
            Language = LanguageName,
            Namespaces = walker.Namespaces.Distinct().ToList(),
            Types = walker.Types,
            Usings = walker.Usings,
            Invocations = walker.Invocations,
            Creations = walker.Creations,
            DiRegistrations = walker.DiRegistrations,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>The ONE syntax walker that extracts everything ACE needs from a C# file.</summary>
    private sealed class ExtractionWalker : CSharpSyntaxWalker
    {
        private static readonly HashSet<string> DiRegistrationMethods = new(StringComparer.Ordinal)
        {
            "AddSingleton",
            "AddScoped",
            "AddTransient",
        };

        private readonly SyntaxTree _tree;
        private readonly string _filePath;
        private readonly List<string> _namespaces = [];
        private readonly List<TypeInfo> _types = [];
        private readonly List<string> _usings = [];
        private readonly List<InvocationSite> _invocations = [];
        private readonly List<CreationSite> _creations = [];
        private readonly List<DiRegistration> _diRegistrations = [];
        private readonly Stack<string> _namespaceStack = new();
        private readonly Stack<TypeContext> _typeStack = new();
        private readonly Stack<string> _memberStack = new();

        public ExtractionWalker(SyntaxTree tree, string filePath)
        {
            _tree = tree;
            _filePath = filePath;
        }

        public List<string> Namespaces => _namespaces;

        public List<TypeInfo> Types => _types;

        public List<string> Usings => _usings;

        public List<InvocationSite> Invocations => _invocations;

        public List<CreationSite> Creations => _creations;

        public List<DiRegistration> DiRegistrations => _diRegistrations;

        public override void VisitUsingDirective(UsingDirectiveSyntax node)
        {
            if (node.Alias is null && node.Name is not null)
            {
                _usings.Add(node.Name.ToString());
            }

            base.VisitUsingDirective(node);
        }

        public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
        {
            _namespaces.Add(node.Name.ToString());
            _namespaceStack.Push(node.Name.ToString());
            base.VisitNamespaceDeclaration(node);
            _namespaceStack.Pop();
        }

        public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
        {
            _namespaces.Add(node.Name.ToString());
            _namespaceStack.Push(node.Name.ToString());
            base.VisitFileScopedNamespaceDeclaration(node);
            _namespaceStack.Pop();
        }

        public override void VisitClassDeclaration(ClassDeclarationSyntax node)
            => VisitTypeDeclaration(node, node.Identifier.Text, TypeKind.Class, node.BaseList, node.AttributeLists, node.Modifiers, node.Span, () => base.VisitClassDeclaration(node));

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
            => VisitTypeDeclaration(node, node.Identifier.Text, TypeKind.Interface, node.BaseList, node.AttributeLists, node.Modifiers, node.Span, () => base.VisitInterfaceDeclaration(node));

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
            => VisitTypeDeclaration(node, node.Identifier.Text, TypeKind.Record, node.BaseList, node.AttributeLists, node.Modifiers, node.Span, () => base.VisitRecordDeclaration(node));

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
            => VisitTypeDeclaration(node, node.Identifier.Text, TypeKind.Struct, node.BaseList, node.AttributeLists, node.Modifiers, node.Span, () => base.VisitStructDeclaration(node));

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
            => VisitTypeDeclaration(node, node.Identifier.Text, TypeKind.Enum, node.BaseList, node.AttributeLists, node.Modifiers, node.Span, () => base.VisitEnumDeclaration(node));

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (_typeStack.Count == 0)
            {
                base.VisitMethodDeclaration(node);
                return;
            }

            var parameterTypes = node.ParameterList.Parameters
                .Select(p => p.Type?.ToString() ?? string.Empty)
                .Where(t => t.Length > 0)
                .ToList();

            var member = new MemberInfo
            {
                Name = node.Identifier.Text,
                Kind = MemberKind.Method,
                Signature = NormalizeWhitespace($"{node.ReturnType} {node.Identifier}{node.ParameterList}"),
                ReturnType = node.ReturnType.ToString(),
                ParameterTypes = parameterTypes,
                Attributes = AttributeNames(node.AttributeLists),
                StartLine = Line(node),
                IsPublic = IsPublic(node.Modifiers),
            };

            VisitMember(member, () => base.VisitMethodDeclaration(node));
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            if (_typeStack.Count == 0)
            {
                base.VisitConstructorDeclaration(node);
                return;
            }

            var parameterTypes = node.ParameterList.Parameters
                .Select(p => p.Type?.ToString() ?? string.Empty)
                .Where(t => t.Length > 0)
                .ToList();

            var member = new MemberInfo
            {
                Name = node.Identifier.Text,
                Kind = MemberKind.Constructor,
                Signature = NormalizeWhitespace($"{node.Identifier}{node.ParameterList}"),
                ParameterTypes = parameterTypes,
                Attributes = AttributeNames(node.AttributeLists),
                StartLine = Line(node),
                IsPublic = IsPublic(node.Modifiers),
            };

            VisitMember(member, () => base.VisitConstructorDeclaration(node));
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (_typeStack.Count > 0)
            {
                _typeStack.Peek().Members.Add(new MemberInfo
                {
                    Name = node.Identifier.Text,
                    Kind = MemberKind.Property,
                    Signature = NormalizeWhitespace($"{node.Type} {node.Identifier}"),
                    ReturnType = node.Type.ToString(),
                    Attributes = AttributeNames(node.AttributeLists),
                    StartLine = Line(node),
                    IsPublic = IsPublic(node.Modifiers),
                });
            }

            base.VisitPropertyDeclaration(node);
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            if (_typeStack.Count > 0)
            {
                foreach (var variable in node.Declaration.Variables)
                {
                    _typeStack.Peek().Members.Add(new MemberInfo
                    {
                        Name = variable.Identifier.Text,
                        Kind = MemberKind.Field,
                        Signature = NormalizeWhitespace($"{node.Declaration.Type} {variable.Identifier}"),
                        ReturnType = node.Declaration.Type.ToString(),
                        StartLine = Line(node),
                        IsPublic = IsPublic(node.Modifiers),
                    });
                }
            }

            base.VisitFieldDeclaration(node);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var calleeName = ExtractCalleeName(node.Expression);
            if (calleeName is { Length: > 0 })
            {
                _invocations.Add(new InvocationSite
                {
                    CalleeName = calleeName,
                    ContainingType = CurrentTypeName,
                    ContainingMember = _memberStack.Count > 0 ? _memberStack.Peek() : string.Empty,
                    File = _filePath,
                    Line = Line(node),
                });

                if (DiRegistrationMethods.Contains(calleeName))
                {
                    _diRegistrations.Add(new DiRegistration
                    {
                        MethodName = calleeName,
                        TypeArguments = ExtractTypeArguments(node.Expression),
                        ContainingType = CurrentTypeName,
                        ContainingMember = _memberStack.Count > 0 ? _memberStack.Peek() : string.Empty,
                        File = _filePath,
                        Line = Line(node),
                    });
                }
            }

            base.VisitInvocationExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            var typeName = SimpleTypeName(node.Type);
            if (typeName.Length > 0)
            {
                _creations.Add(new CreationSite
                {
                    TypeName = typeName,
                    ContainingType = CurrentTypeName,
                    ContainingMember = _memberStack.Count > 0 ? _memberStack.Peek() : string.Empty,
                    File = _filePath,
                    Line = Line(node),
                });
            }

            base.VisitObjectCreationExpression(node);
        }

        private string CurrentTypeName => _typeStack.Count > 0 ? _typeStack.Peek().Name : string.Empty;

        private void VisitTypeDeclaration(
            SyntaxNode node,
            string name,
            TypeKind kind,
            BaseListSyntax? baseList,
            SyntaxList<AttributeListSyntax> attributeLists,
            SyntaxTokenList modifiers,
            Microsoft.CodeAnalysis.Text.TextSpan span,
            Action visitChildren)
        {
            var lineSpan = _tree.GetLineSpan(span);
            var context = new TypeContext
            {
                Name = name,
                Namespace = string.Join('.', _namespaceStack.Reverse()),
                Kind = kind,
                BaseTypes = baseList?.Types.Select(t => SimpleTypeName(t.Type)).Where(n => n.Length > 0).ToList() ?? [],
                Attributes = AttributeNames(attributeLists),
                StartLine = lineSpan.StartLinePosition.Line + 1,
                EndLine = lineSpan.EndLinePosition.Line + 1,
                IsPublic = IsPublic(modifiers),
            };

            _typeStack.Push(context);
            visitChildren();
            _typeStack.Pop();

            var typeInfo = new TypeInfo
            {
                Name = context.Name,
                Kind = context.Kind,
                Namespace = context.Namespace,
                BaseTypes = context.BaseTypes,
                Attributes = context.Attributes,
                StartLine = context.StartLine,
                EndLine = context.EndLine,
                IsPublic = context.IsPublic,
                Members = context.Members,
            };

            _types.Add(typeInfo);
        }

        private void VisitMember(MemberInfo member, Action visitChildren)
        {
            _typeStack.Peek().Members.Add(member);
            _memberStack.Push(member.Name);
            visitChildren();
            _memberStack.Pop();
        }

        private int Line(SyntaxNode node) => _tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

        private static bool IsPublic(SyntaxTokenList modifiers)
            => modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));

        private static List<string> AttributeNames(SyntaxList<AttributeListSyntax> attributeLists)
            => attributeLists
                .SelectMany(list => list.Attributes)
                .Select(a => SimpleTypeName(a.Name))
                .Where(n => n.Length > 0)
                .ToList();

        private static string? ExtractCalleeName(ExpressionSyntax expression) => expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            MemberAccessExpressionSyntax memberAccess => SimpleMemberName(memberAccess.Name),
            MemberBindingExpressionSyntax memberBinding => SimpleMemberName(memberBinding.Name),
            ConditionalAccessExpressionSyntax conditional => ExtractCalleeName(conditional.WhenNotNull),
            _ => null,
        };

        private static string SimpleMemberName(SimpleNameSyntax name) => name switch
        {
            GenericNameSyntax generic => generic.Identifier.Text,
            _ => name.Identifier.Text,
        };

        private static IReadOnlyList<string> ExtractTypeArguments(ExpressionSyntax expression)
        {
            if (expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic })
            {
                return generic.TypeArgumentList.Arguments
                    .Select(SimpleTypeName)
                    .Where(n => n.Length > 0)
                    .ToList();
            }

            return [];
        }

        /// <summary>Simple (unqualified, non-generic) name of a type reference.</summary>
        private static string SimpleTypeName(TypeSyntax type) => type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            QualifiedNameSyntax qualified => SimpleTypeName(qualified.Right),
            NullableTypeSyntax nullable => SimpleTypeName(nullable.ElementType),
            PredefinedTypeSyntax predefined => predefined.Keyword.Text,
            _ => string.Empty,
        };

        private static string NormalizeWhitespace(string text)
            => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private sealed class TypeContext
        {
            public required string Name { get; init; }

            public required string Namespace { get; init; }

            public required TypeKind Kind { get; init; }

            public required IReadOnlyList<string> BaseTypes { get; init; }

            public required IReadOnlyList<string> Attributes { get; init; }

            public required int StartLine { get; init; }

            public required int EndLine { get; init; }

            public required bool IsPublic { get; init; }

            public List<MemberInfo> Members { get; } = [];
        }
    }
}
