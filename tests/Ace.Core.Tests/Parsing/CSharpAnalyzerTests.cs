using Ace.Core.Parsing;
using Ace.Core.Parsing.CSharp;

namespace Ace.Core.Tests.Parsing;

public sealed class CSharpAnalyzerTests
{
    private readonly CSharpAnalyzer _analyzer = new();

    private static string SampleFile(params string[] segments)
        => File.ReadAllText(Path.Combine([TestPaths.SampleRepo, .. segments]));

    private Task<FileAnalysis> AnalyzeSampleFile(params string[] segments)
    {
        var relative = string.Join('/', segments);
        return _analyzer.AnalyzeAsync(relative, SampleFile(segments));
    }

    [Fact]
    public void CanHandle_OnlyCSharpFiles()
    {
        Assert.True(_analyzer.CanHandle("src/A.cs"));
        Assert.True(_analyzer.CanHandle("src/A.CS"));
        Assert.False(_analyzer.CanHandle("src/a.ts"));
        Assert.False(_analyzer.CanHandle("src/a.cs.bak"));
        Assert.Equal("C#", _analyzer.Language);
    }

    [Fact]
    public async Task Analyze_CustomerService_ExtractsTypeMembersAndCalls()
    {
        var analysis = await AnalyzeSampleFile("src", "Customer.Services", "CustomerService.cs");

        Assert.False(analysis.HasDiagnostics);
        Assert.Equal("C#", analysis.Language);
        Assert.Equal("src/Customer.Services/CustomerService.cs", analysis.FilePath);

        Assert.Contains("Customer.Services", analysis.Namespaces);
        Assert.Contains("Customer.Domain", analysis.Usings);

        var type = Assert.Single(analysis.Types);
        Assert.Equal("CustomerService", type.Name);
        Assert.Equal(TypeKind.Class, type.Kind);
        Assert.Equal("Customer.Services", type.Namespace);
        Assert.True(type.IsPublic);
        Assert.True(type.StartLine > 0);
        Assert.True(type.EndLine >= type.StartLine);

        Assert.Contains(type.Members, m => m.Name == "CalculateDiscount" && m.Kind == MemberKind.Method && m.IsPublic);
        Assert.Contains(type.Members, m => m.Name == "GetCustomer" && m.Kind == MemberKind.Method);
        Assert.Contains(type.Members, m => m.Name == "CustomerService" && m.Kind == MemberKind.Constructor);
        Assert.Contains(type.Members, m => m.Name == "_repository" && m.Kind == MemberKind.Field);

        var calculateDiscount = type.Members.First(m => m.Name == "CalculateDiscount");
        Assert.Contains("CalculateDiscount", calculateDiscount.Signature);
        Assert.Equal("decimal", calculateDiscount.ReturnType);
        Assert.Contains("Customer", calculateDiscount.ParameterTypes);

        // Invocation sites are recorded with the callee identifier and file:line.
        Assert.Contains(analysis.Invocations, i => i.CalleeName == "Validate" && i.ContainingMember == "CalculateDiscount");
        Assert.Contains(analysis.Invocations, i => i.CalleeName == "GetById" && i.Line > 0);
        Assert.All(analysis.Invocations, i => Assert.Equal("src/Customer.Services/CustomerService.cs", i.File));
    }

    [Fact]
    public async Task Analyze_FileScopedAndBlockNamespaces_BothDetected()
    {
        var fileScoped = await AnalyzeSampleFile("src", "Customer.Domain", "Customer.cs");
        Assert.Contains("Customer.Domain", fileScoped.Namespaces);

        var blockScoped = await AnalyzeSampleFile("src", "Customer.Domain", "CustomerTier.cs");
        Assert.Contains("Customer.Domain", blockScoped.Namespaces);
        var tier = Assert.Single(blockScoped.Types);
        Assert.Equal(TypeKind.Enum, tier.Kind);
    }

    [Fact]
    public async Task Analyze_Controller_ExtractsAttributesBaseTypeAndMembers()
    {
        var analysis = await AnalyzeSampleFile("src", "Customer.Api", "CustomerController.cs");

        var controller = Assert.Single(analysis.Types);
        Assert.Contains("ApiController", controller.Attributes);
        Assert.Contains("Route", controller.Attributes);
        Assert.Contains("ControllerBase", controller.BaseTypes);

        var getCustomer = controller.Members.First(m => m.Name == "GetCustomer");
        Assert.Contains("HttpGet", getCustomer.Attributes);
    }

    [Fact]
    public async Task Analyze_Interface_ExtractsInterfaceKindAndBaseListResolutionInput()
    {
        var analysis = await AnalyzeSampleFile("src", "Customer.Domain", "InMemoryCustomerRepository.cs");

        var repository = Assert.Single(analysis.Types);
        Assert.Contains("ICustomerRepository", repository.BaseTypes);

        var interfaceAnalysis = await AnalyzeSampleFile("src", "Customer.Domain", "ICustomerRepository.cs");
        var iface = Assert.Single(interfaceAnalysis.Types);
        Assert.Equal(TypeKind.Interface, iface.Kind);
        Assert.Contains(iface.Members, m => m.Name == "GetById");
    }

    [Fact]
    public async Task Analyze_TestFile_ExtractsCreationsAndFactAttributes()
    {
        var analysis = await AnalyzeSampleFile("tests", "Customer.Services.Tests", "CustomerServiceTests.cs");

        Assert.Contains(analysis.Creations, c => c.TypeName == "InMemoryCustomerRepository");
        Assert.Contains(analysis.Creations, c => c.TypeName == "Customer");
        Assert.Contains(analysis.Invocations, i => i.CalleeName == "CalculateDiscount");

        var testMethods = analysis.Types.Single().Members.Where(m => m.Kind == MemberKind.Method).ToList();
        Assert.Contains(testMethods, m => m.Attributes.Contains("Fact"));
    }

    [Fact]
    public async Task Analyze_Startup_DetectsDependencyInjectionRegistrations()
    {
        var analysis = await AnalyzeSampleFile("src", "Customer.Api", "Startup.cs");

        Assert.Equal(4, analysis.DiRegistrations.Count);
        Assert.Contains(analysis.DiRegistrations, r => r.MethodName == "AddSingleton" && r.TypeArguments.Contains("InMemoryCustomerRepository"));
        Assert.Contains(analysis.DiRegistrations, r => r.MethodName == "AddScoped" && r.TypeArguments.Contains("ICustomerRepository") && r.TypeArguments.Contains("InMemoryCustomerRepository"));
        Assert.Contains(analysis.DiRegistrations, r => r.MethodName == "AddScoped" && r.TypeArguments.Contains("CustomerService"));
        Assert.Contains(analysis.DiRegistrations, r => r.MethodName == "AddTransient" && r.TypeArguments.Contains("OrderService"));
        Assert.All(analysis.DiRegistrations, r => Assert.Equal("ConfigureServices", r.ContainingMember));
    }

    [Fact]
    public async Task Analyze_MalformedFile_ReturnsDiagnosticsNotException()
    {
        var analysis = await AnalyzeSampleFile("tests", "Customer.Services.Tests", "LegacyNotes.cs");

        Assert.True(analysis.HasDiagnostics);
        Assert.Contains(analysis.Diagnostics, d => d.Severity == "error" && d.Line > 0);
        // Partial analysis is still available where the parser could recover.
        Assert.Equal("tests/Customer.Services.Tests/LegacyNotes.cs", analysis.FilePath);
    }

    [Fact]
    public async Task Analyze_GarbageInput_ReturnsAnalysisWithDiagnostics()
    {
        var analysis = await _analyzer.AnalyzeAsync("garbage.cs", "}}} this is { not c# at all (((\0\0");

        Assert.True(analysis.HasDiagnostics);
        Assert.Empty(analysis.Types);
    }

    [Fact]
    public async Task Analyze_EmptyContent_ProducesCleanEmptyAnalysis()
    {
        var analysis = await _analyzer.AnalyzeAsync("empty.cs", string.Empty);

        Assert.False(analysis.HasDiagnostics);
        Assert.Empty(analysis.Types);
        Assert.Empty(analysis.Invocations);
    }
}
