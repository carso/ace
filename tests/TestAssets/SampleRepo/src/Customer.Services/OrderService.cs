using Customer.Domain;

namespace Customer.Services;

/// <summary>Order pricing pipeline; depends on <see cref="CustomerService"/>.</summary>
public class OrderService
{
    private readonly CustomerService _customerService;

    public OrderService(CustomerService customerService)
    {
        _customerService = customerService;
    }

    /// <summary>Computes the order total after applying the customer's tier discount.</summary>
    public decimal PlaceOrder(int customerId, decimal grossTotal)
    {
        var customer = _customerService.GetCustomer(customerId);
        if (customer == null)
        {
            return grossTotal;
        }

        var discountPercent = _customerService.CalculateDiscount(customer);
        return grossTotal - (grossTotal * discountPercent / 100m);
    }
}
