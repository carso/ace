using Customer.Domain;
using Customer.Services;
using Xunit;

namespace Customer.Services.Tests;

public class OrderServiceTests
{
    private static (OrderService Orders, InMemoryCustomerRepository Repository) CreateSystem()
    {
        var repository = new InMemoryCustomerRepository();
        var customerService = new CustomerService(repository);
        var orderService = new OrderService(customerService);
        return (orderService, repository);
    }

    [Fact]
    public void PlaceOrder_GoldCustomer_AppliesDiscount()
    {
        var (orders, repository) = CreateSystem();
        var customer = new Customer { Name = "Grace", Email = "grace@example.com", Tier = CustomerTier.Gold };
        repository.Save(customer);

        var total = orders.PlaceOrder(customer.Id, 100m);

        Assert.Equal(85m, total);
    }

    [Fact]
    public void PlaceOrder_UnknownCustomer_ReturnsGrossTotal()
    {
        var (orders, _) = CreateSystem();

        var total = orders.PlaceOrder(999, 100m);

        Assert.Equal(100m, total);
    }
}
