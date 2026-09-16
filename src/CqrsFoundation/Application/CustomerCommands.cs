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
    private const string Operation = "customers.create.v1";

    public static async Task<Guid> Handle(
        CreateCustomer command,
        Guid actorId,
        IDocumentStore store,
        CommandMetadata metadata,
        CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        if (name.Length == 0)
        {
            throw new BusinessRuleException("Customer name is required.");
        }

        var tenancyId = SystemTenancy.For(command.TenantId);
        var fingerprint = CommandIdempotency.Fingerprint(Operation, name);
        await using var session = store.LightweightSession(tenancyId);
        var existing = await CommandIdempotency.LoadExisting(
            session,
            actorId,
            metadata,
            fingerprint,
            cancellationToken);
        if (existing is not null)
        {
            return CommandIdempotency.RequireResourceId(existing);
        }

        var customerId = CommandIdempotency.NewResourceId(actorId, Operation, metadata);
        AuditMetadata.Apply(session, actorId, metadata);
        session.Events.StartStream<CustomerAggregate>(
            customerId,
            new CustomerCreated(customerId, name));
        CommandIdempotency.Stage(session, actorId, metadata, fingerprint, customerId);

        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception)
        {
            var recovered = await CommandIdempotency.RecoverCommitted(
                store,
                tenancyId,
                actorId,
                metadata,
                fingerprint,
                cancellationToken);
            if (recovered is not null)
            {
                return CommandIdempotency.RequireResourceId(recovered);
            }

            throw;
        }

        return customerId;
    }
}

public static class RenameCustomerHandler
{
    private const string Operation = "customers.rename.v1";

    public static async Task Handle(
        RenameCustomer command,
        Guid actorId,
        IDocumentStore store,
        CommandMetadata metadata,
        CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        if (name.Length == 0)
        {
            throw new BusinessRuleException("Customer name is required.");
        }

        var tenancyId = SystemTenancy.For(command.TenantId);
        var fingerprint = CommandIdempotency.Fingerprint(
            Operation,
            command.CustomerId.ToString("D"),
            name);
        await using var session = store.LightweightSession(tenancyId);
        if (await CommandIdempotency.LoadExisting(
                session,
                actorId,
                metadata,
                fingerprint,
                cancellationToken) is not null)
        {
            return;
        }

        AuditMetadata.Apply(session, actorId, metadata);
        var stream = await session.Events.FetchForWriting<CustomerAggregate>(command.CustomerId, cancellationToken);
        var customer = stream.Aggregate ?? throw new KeyNotFoundException("Customer not found.");
        var events = customer.Rename(name);
        if (events.Count == 0 && metadata.IdempotencyKey is null)
        {
            return;
        }

        if (events.Count == 0)
        {
            stream.AlwaysEnforceConsistency = true;
        }
        else
        {
            stream.AppendMany(events);
        }

        CommandIdempotency.Stage(session, actorId, metadata, fingerprint);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception)
        {
            if (await CommandIdempotency.RecoverCommitted(
                    store,
                    tenancyId,
                    actorId,
                    metadata,
                    fingerprint,
                    cancellationToken) is not null)
            {
                return;
            }

            throw;
        }
    }
}

public static class DeactivateCustomerHandler
{
    private const string Operation = "customers.deactivate.v1";

    public static async Task Handle(
        DeactivateCustomer command,
        Guid actorId,
        IDocumentStore store,
        CommandMetadata metadata,
        CancellationToken cancellationToken)
    {
        var tenancyId = SystemTenancy.For(command.TenantId);
        var fingerprint = CommandIdempotency.Fingerprint(
            Operation,
            command.CustomerId.ToString("D"));
        await using var session = store.LightweightSession(tenancyId);
        if (await CommandIdempotency.LoadExisting(
                session,
                actorId,
                metadata,
                fingerprint,
                cancellationToken) is not null)
        {
            return;
        }

        AuditMetadata.Apply(session, actorId, metadata);
        var stream = await session.Events.FetchForWriting<CustomerAggregate>(command.CustomerId, cancellationToken);
        var customer = stream.Aggregate ?? throw new KeyNotFoundException("Customer not found.");
        var events = customer.Deactivate();
        if (events.Count == 0 && metadata.IdempotencyKey is null)
        {
            return;
        }

        if (events.Count == 0)
        {
            stream.AlwaysEnforceConsistency = true;
        }
        else
        {
            stream.AppendMany(events);
        }

        CommandIdempotency.Stage(session, actorId, metadata, fingerprint);
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (Exception)
        {
            if (await CommandIdempotency.RecoverCommitted(
                    store,
                    tenancyId,
                    actorId,
                    metadata,
                    fingerprint,
                    cancellationToken) is not null)
            {
                return;
            }

            throw;
        }
    }
}
