namespace Customer.Domain;

/// <summary>Repository abstraction for customer persistence.</summary>
public interface ICustomerRepository
{
    Customer? GetById(int id);

    IReadOnlyList<Customer> GetAll();

    void Save(Customer customer);
}
