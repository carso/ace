using Ace.Core.Configuration;
using Ace.Core.Engines;
using Ace.Core.Graph;
using Ace.Core.Models;
using Ace.Core.Tests.Graph;

namespace Ace.Core.Tests.Engines;

public sealed class ArchitectureEngineTests
{
    private static InMemoryCodeGraph BuildLayeredGraph()
    {
        var graph = new InMemoryCodeGraph();
        graph.AddNode(new GraphNode { Id = "App:App.CustomerController", Name = "CustomerController", Type = NodeType.Class, Project = "App", FilePath = "src/CustomerController.cs" });
        graph.AddNode(new GraphNode { Id = "App:App.OrderService", Name = "OrderService", Type = NodeType.Class, Project = "App", FilePath = "src/OrderService.cs" });
        graph.AddNode(new GraphNode { Id = "App:App.CustomerRepository", Name = "CustomerRepository", Type = NodeType.Class, Project = "App", FilePath = "src/CustomerRepository.cs" });

        // Allowed: Controller -> Service -> Repository.
        graph.AddEdge(new GraphEdge { SourceId = "App:App.CustomerController", TargetId = "App:App.OrderService", Type = EdgeType.Calls, Evidence = "invocation", Location = "src/CustomerController.cs:10" });
        graph.AddEdge(new GraphEdge { SourceId = "App:App.OrderService", TargetId = "App:App.CustomerRepository", Type = EdgeType.Calls, Evidence = "invocation", Location = "src/OrderService.cs:20" });
        return graph;
    }

    [Fact]
    public void Analyze_DetectsInjectedRepositoryToControllerViolation()
    {
        var graph = BuildLayeredGraph();
        graph.AddEdge(new GraphEdge
        {
            SourceId = "App:App.CustomerRepository",
            TargetId = "App:App.CustomerController",
            Type = EdgeType.Calls,
            Evidence = "invocation",
            Location = "src/CustomerRepository.cs:30",
        });

        var violations = new ArchitectureEngine().Analyze(graph, new AceOptions { EnableArchitectureAnalysis = true });

        var violation = Assert.Single(violations);
        Assert.Equal("layered-architecture", violation.Rule);
        Assert.Equal("CustomerRepository", violation.Source);
        Assert.Equal("CustomerController", violation.Target);
        Assert.Equal(EdgeType.Calls, violation.EdgeType);
        Assert.Equal(ArchitectureEngine.SeverityViolation, violation.Severity); // gap of two layers
        Assert.Equal("src/CustomerRepository.cs:30", violation.Location);
        Assert.False(string.IsNullOrEmpty(violation.Message));
    }

    [Fact]
    public void Analyze_AllowsControllerServiceRepositoryDirection()
    {
        var violations = new ArchitectureEngine().Analyze(BuildLayeredGraph(), new AceOptions { EnableArchitectureAnalysis = true });

        Assert.Empty(violations);
    }

    [Fact]
    public void Analyze_ReturnsEmptyWhenDisabled()
    {
        var graph = BuildLayeredGraph();
        graph.AddEdge(new GraphEdge { SourceId = "App:App.CustomerRepository", TargetId = "App:App.CustomerController", Type = EdgeType.Calls });

        var violations = new ArchitectureEngine().Analyze(graph, new AceOptions { EnableArchitectureAnalysis = false });

        Assert.Empty(violations);
    }

    [Fact]
    public void Analyze_FlagsDomainToInfrastructureAsPotentialViolation()
    {
        var graph = new InMemoryCodeGraph();
        graph.AddNode(new GraphNode { Id = "Core:Core.Domain.Order", Name = "Order", Type = NodeType.Class, Project = "Core", Namespace = "Core.Domain" });
        graph.AddNode(new GraphNode { Id = "Core:Core.Infrastructure.PaymentGateway", Name = "PaymentGateway", Type = NodeType.Class, Project = "Core", Namespace = "Core.Infrastructure" });
        graph.AddEdge(new GraphEdge { SourceId = "Core:Core.Domain.Order", TargetId = "Core:Core.Infrastructure.PaymentGateway", Type = EdgeType.References, Evidence = "field-type", Location = "src/Order.cs:5" });

        var violations = new ArchitectureEngine().Analyze(graph, new AceOptions { EnableArchitectureAnalysis = true });

        var violation = Assert.Single(violations);
        Assert.Equal("domain-independence", violation.Rule);
        Assert.Equal(ArchitectureEngine.SeverityPotentialViolation, violation.Severity); // single-step inversion
    }

    [Fact]
    public void Analyze_RespectsCustomRules()
    {
        var graph = BuildLayeredGraph();
        var options = new AceOptions
        {
            EnableArchitectureAnalysis = true,
            ArchitectureRules =
            [
                // Layers are ordered outer-to-inner; declaring Repository as the outer
                // layer forbids the Service -> Repository dependency present in the graph.
                new ArchitectureRule { Name = "no-service-to-repository", Layers = ["Repository", "Service"] },
            ],
        };

        var violations = new ArchitectureEngine().Analyze(graph, options);

        var violation = Assert.Single(violations);
        Assert.Equal("no-service-to-repository", violation.Rule);
        Assert.Equal("OrderService", violation.Source);
        Assert.Equal("CustomerRepository", violation.Target);
    }

    [Fact]
    public void Analyze_SampleRepoHasNoDefaultRuleViolations()
    {
        var violations = new ArchitectureEngine().Analyze(
            SampleRepoGraph.Data.Graph,
            new AceOptions { EnableArchitectureAnalysis = true });

        Assert.Empty(violations);
    }

    [Fact]
    public void ClassifyLayer_UsesNamingConventions()
    {
        Assert.Equal("Controller", ArchitectureEngine.ClassifyLayer(new GraphNode { Id = "x", Name = "CustomerController" }));
        Assert.Equal("Repository", ArchitectureEngine.ClassifyLayer(new GraphNode { Id = "x", Name = "ICustomerRepository" }));
        Assert.Equal("Service", ArchitectureEngine.ClassifyLayer(new GraphNode { Id = "x", Name = "OrderService" }));
        Assert.Equal("Domain", ArchitectureEngine.ClassifyLayer(new GraphNode { Id = "x", Name = "Order", Namespace = "Shop.Domain" }));
        Assert.Equal("Infrastructure", ArchitectureEngine.ClassifyLayer(new GraphNode { Id = "x", Name = "Gateway", Project = "Shop.Infrastructure" }));
        Assert.Null(ArchitectureEngine.ClassifyLayer(new GraphNode { Id = "x", Name = "Order" }));
    }
}
