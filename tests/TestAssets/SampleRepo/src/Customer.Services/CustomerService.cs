using Customer.Domain;

namespace Customer.Services;

/// <summary>Business logic around customers.</summary>
public class CustomerService
{
    private readonly ICustomerRepository _repository;

    public CustomerService(ICustomerRepository repository)
    {
        _repository = repository;
    }

    /// <summary>Loads a customer by id, or null when missing or inactive.</summary>
    public Customer? GetCustomer(int customerId)
    {
        var customer = _repository.GetById(customerId);
        if (customer == null || !customer.IsActive)
        {
            return null;
        }

        return customer;
    }

    /// <summary>Tier-based discount percentage used by the order pipeline.</summary>
    public decimal CalculateDiscount(Customer customer)
    {
        if (!customer.Validate())
        {
            return 0m;
        }

        return customer.Tier switch
        {
            CustomerTier.Platinum => 20m,
            CustomerTier.Gold => 15m,
            CustomerTier.Silver => 10m,
            _ => 5m,
        };
    }
}
