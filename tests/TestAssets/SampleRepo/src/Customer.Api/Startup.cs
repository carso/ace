using Customer.Domain;
using Customer.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Customer.Api;

/// <summary>Composition root: wires services into the DI container.</summary>
public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<InMemoryCustomerRepository>();
        services.AddScoped<ICustomerRepository, InMemoryCustomerRepository>();
        services.AddScoped<CustomerService>();
        services.AddTransient<OrderService>();
        services.AddControllers();
    }
}
