namespace Customer.Domain;

/// <summary>In-memory implementation of <see cref="ICustomerRepository"/> for samples and tests.</summary>
public class InMemoryCustomerRepository : ICustomerRepository
{
    private readonly Dictionary<int, Customer> _store = new();
    private int _nextId = 1;

    public Customer? GetById(int id) => _store.TryGetValue(id, out var customer) ? customer : null;

    public IReadOnlyList<Customer> GetAll() => _store.Values.ToList();

    public void Save(Customer customer)
    {
        if (customer.Id == 0)
        {
            customer.Id = _nextId++;
        }

        _store[customer.Id] = customer;
    }
}
