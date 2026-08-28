using CqrsFoundation.Domain.Common;

namespace CqrsFoundation.Domain.Customers;

public sealed record CustomerCreated(Guid CustomerId, string Name);
public sealed record CustomerRenamed(string Name);
public sealed record CustomerDeactivated;

public sealed record CustomerAggregate(Guid Id, string Name, bool IsActive)
{
    public static CustomerAggregate Create(CustomerCreated @event) =>
        new(@event.CustomerId, @event.Name, true);

    public static CustomerAggregate Apply(CustomerRenamed @event, CustomerAggregate customer) =>
        customer with { Name = @event.Name };

    public static CustomerAggregate Apply(CustomerDeactivated _, CustomerAggregate customer) =>
        customer with { IsActive = false };

    public IReadOnlyList<object> Rename(string name)
    {
        if (!IsActive)
        {
            throw new BusinessRuleException("Inactive customers cannot be renamed.");
        }

        var normalized = name.Trim();
        if (normalized.Length == 0)
        {
            throw new BusinessRuleException("Customer name is required.");
        }

        return string.Equals(Name, normalized, StringComparison.Ordinal)
            ? []
            : [new CustomerRenamed(normalized)];
    }

    public IReadOnlyList<object> Deactivate() =>
        IsActive ? [new CustomerDeactivated()] : [];
}

public sealed record CustomerView(Guid Id, string Name, bool IsActive)
{
    public static CustomerView Create(CustomerCreated @event) =>
        new(@event.CustomerId, @event.Name, true);

    public static CustomerView Apply(CustomerRenamed @event, CustomerView customer) =>
        customer with { Name = @event.Name };

    public static CustomerView Apply(CustomerDeactivated _, CustomerView customer) =>
        customer with { IsActive = false };
}
