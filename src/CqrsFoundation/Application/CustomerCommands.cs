using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Customers;
using CqrsFoundation.Infrastructure;
using Marten;

namespace CqrsFoundation.Application;

public sealed record CreateCustomer(Guid TenantId, string Name);
public sealed record RenameCustomer(Guid TenantId, Guid CustomerId, string Name);
public sealed record DeactivateCustomer(Guid TenantId, Guid CustomerId);

public static class CreateCustomerHandler
{
    public static async Task<Guid> Handle(
        CreateCustomer command,
        Guid actorId,
        IDocumentStore store,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        if (name.Length == 0)
        {
            throw new BusinessRuleException("Customer name is required.");
        }

        var customerId = Guid.NewGuid();
        await using var session = store.LightweightSession(SystemTenancy.For(command.TenantId));
        AuditMetadata.Apply(session, actorId, correlationId);
        session.Events.StartStream<CustomerAggregate>(
            customerId,
            new CustomerCreated(customerId, name));
        await session.SaveChangesAsync(cancellationToken);
        return customerId;
    }
}

public static class RenameCustomerHandler
{
    public static async Task Handle(
        RenameCustomer command,
        Guid actorId,
        IDocumentStore store,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession(SystemTenancy.For(command.TenantId));
        AuditMetadata.Apply(session, actorId, correlationId);
        var stream = await session.Events.FetchForWriting<CustomerAggregate>(command.CustomerId, cancellationToken);
        var customer = stream.Aggregate ?? throw new KeyNotFoundException("Customer not found.");
        var events = customer.Rename(command.Name);
        if (events.Count == 0)
        {
            return;
        }

        stream.AppendMany(events);
        await session.SaveChangesAsync(cancellationToken);
    }
}

public static class DeactivateCustomerHandler
{
    public static async Task Handle(
        DeactivateCustomer command,
        Guid actorId,
        IDocumentStore store,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession(SystemTenancy.For(command.TenantId));
        AuditMetadata.Apply(session, actorId, correlationId);
        var stream = await session.Events.FetchForWriting<CustomerAggregate>(command.CustomerId, cancellationToken);
        var customer = stream.Aggregate ?? throw new KeyNotFoundException("Customer not found.");
        var events = customer.Deactivate();
        if (events.Count == 0)
        {
            return;
        }

        stream.AppendMany(events);
        await session.SaveChangesAsync(cancellationToken);
    }
}
