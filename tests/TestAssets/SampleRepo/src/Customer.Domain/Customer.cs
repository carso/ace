namespace Customer.Domain;

/// <summary>A customer of the sample shop. Plain domain entity used as a parse fixture.</summary>
public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    public CustomerTier Tier { get; set; } = CustomerTier.Standard;

    public decimal TotalSpent { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Validates the invariants of this customer.</summary>
    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(Email) || !Email.Contains('@'))
        {
            return false;
        }

        return TotalSpent >= 0m;
    }
}
