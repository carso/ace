using Customer.Domain;
using Customer.Services;
using Xunit;

namespace Customer.Services.Tests;

public class CustomerServiceTests
{
    private static CustomerService CreateService()
    {
        var repository = new InMemoryCustomerRepository();
        return new CustomerService(repository);
    }

    [Fact]
    public void CalculateDiscount_GoldCustomer_Returns15Percent()
    {
        var service = CreateService();
        var customer = new Customer { Name = "Ada", Email = "ada@example.com", Tier = CustomerTier.Gold };

        var discount = service.CalculateDiscount(customer);

        Assert.Equal(15m, discount);
    }

    [Fact]
    public void CalculateDiscount_InvalidCustomer_ReturnsZero()
    {
        var service = CreateService();
        var customer = new Customer { Name = "", Email = "" };

        var discount = service.CalculateDiscount(customer);

        Assert.Equal(0m, discount);
    }

    [Fact]
    public void GetCustomer_UnknownId_ReturnsNull()
    {
        var service = CreateService();

        var customer = service.GetCustomer(42);

        Assert.Null(customer);
    }
}
