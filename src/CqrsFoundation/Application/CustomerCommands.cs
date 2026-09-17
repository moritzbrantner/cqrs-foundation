using System.Globalization;
using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Customers;
using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Infrastructure;
using Marten;

namespace CqrsFoundation.Application;

public sealed record CreateCustomer(Guid TenantId, string Name);
public sealed record RenameCustomer(Guid TenantId, Guid CustomerId, string Name, long? ExpectedVersion = null);
public sealed record DeactivateCustomer(Guid TenantId, Guid CustomerId, long? ExpectedVersion = null);

public static class CreateCustomerHandler
{
    private const string Operation = "customers.create.v1";
    private const long CreatedVersion = 1;

    public static async Task<CreatedResource> Handle(
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
        await TenantAuthorization.RequireCommandPermission(
            session,
            command.TenantId,
            actorId,
            TenantPermissions.CustomersWrite,
            cancellationToken);

        var existing = await CommandIdempotency.LoadExisting(
            session,
            actorId,
            metadata,
            fingerprint,
            cancellationToken);
        if (existing is not null)
        {
            await session.SaveChangesAsync(cancellationToken);
            return new CreatedResource(
                CommandIdempotency.RequireResourceId(existing),
                CommandIdempotency.RequireResultVersion(existing));
        }

        var customerId = CommandIdempotency.NewResourceId(actorId, Operation, metadata);
        AuditMetadata.Apply(session, actorId, metadata);
        session.Events.StartStream<CustomerAggregate>(
            customerId,
            new CustomerCreated(customerId, name));
        CommandIdempotency.Stage(
            session,
            actorId,
            metadata,
            fingerprint,
            customerId,
            CreatedVersion);

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
                await TenantAuthorization.RequireCurrentPermission(
                    store,
                    command.TenantId,
                    actorId,
                    TenantPermissions.CustomersWrite,
                    cancellationToken);
                return new CreatedResource(
                    CommandIdempotency.RequireResourceId(recovered),
                    CommandIdempotency.RequireResultVersion(recovered));
            }

            throw;
        }

        return new CreatedResource(customerId, CreatedVersion);
    }
}

public static class RenameCustomerHandler
{
    private const string Operation = "customers.rename.v2";

    public static async Task<MutationResult> Handle(
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
            name,
            VersionPart(command.ExpectedVersion));
        await using var session = store.LightweightSession(tenancyId);
        await TenantAuthorization.RequireCommandPermission(
            session,
            command.TenantId,
            actorId,
            TenantPermissions.CustomersWrite,
            cancellationToken);

        var existing = await CommandIdempotency.LoadExisting(
            session,
            actorId,
            metadata,
            fingerprint,
            cancellationToken);
        if (existing is not null)
        {
            await session.SaveChangesAsync(cancellationToken);
            return new MutationResult(CommandIdempotency.RequireResultVersion(existing));
        }

        AuditMetadata.Apply(session, actorId, metadata);
        var stream = await session.Events.FetchForWriting<CustomerAggregate>(
            command.CustomerId,
            cancellationToken);
        var customer = stream.Aggregate ?? throw new KeyNotFoundException("Customer not found.");
        await ConcurrencyPreconditions.EnsureExpectedVersion(
            session,
            command.CustomerId,
            command.ExpectedVersion,
            cancellationToken);

        var events = customer.Rename(name);
        if (events.Count == 0)
        {
            stream.AlwaysEnforceConsistency = true;
        }
        else
        {
            stream.AppendMany(events);
        }

        var resultVersion = stream.CurrentVersion;
        CommandIdempotency.Stage(
            session,
            actorId,
            metadata,
            fingerprint,
            resultVersion: resultVersion);
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
                await TenantAuthorization.RequireCurrentPermission(
                    store,
                    command.TenantId,
                    actorId,
                    TenantPermissions.CustomersWrite,
                    cancellationToken);
                return new MutationResult(CommandIdempotency.RequireResultVersion(recovered));
            }

            throw;
        }

        return new MutationResult(resultVersion);
    }

    private static string VersionPart(long? version) =>
        version?.ToString(CultureInfo.InvariantCulture) ?? "unconditional";
}

public static class DeactivateCustomerHandler
{
    private const string Operation = "customers.deactivate.v2";

    public static async Task<MutationResult> Handle(
        DeactivateCustomer command,
        Guid actorId,
        IDocumentStore store,
        CommandMetadata metadata,
        CancellationToken cancellationToken)
    {
        var tenancyId = SystemTenancy.For(command.TenantId);
        var fingerprint = CommandIdempotency.Fingerprint(
            Operation,
            command.CustomerId.ToString("D"),
            command.ExpectedVersion?.ToString(CultureInfo.InvariantCulture) ?? "unconditional");
        await using var session = store.LightweightSession(tenancyId);
        await TenantAuthorization.RequireCommandPermission(
            session,
            command.TenantId,
            actorId,
            TenantPermissions.CustomersWrite,
            cancellationToken);

        var existing = await CommandIdempotency.LoadExisting(
            session,
            actorId,
            metadata,
            fingerprint,
            cancellationToken);
        if (existing is not null)
        {
            await session.SaveChangesAsync(cancellationToken);
            return new MutationResult(CommandIdempotency.RequireResultVersion(existing));
        }

        AuditMetadata.Apply(session, actorId, metadata);
        var stream = await session.Events.FetchForWriting<CustomerAggregate>(
            command.CustomerId,
            cancellationToken);
        var customer = stream.Aggregate ?? throw new KeyNotFoundException("Customer not found.");
        await ConcurrencyPreconditions.EnsureExpectedVersion(
            session,
            command.CustomerId,
            command.ExpectedVersion,
            cancellationToken);

        var events = customer.Deactivate();
        if (events.Count == 0)
        {
            stream.AlwaysEnforceConsistency = true;
        }
        else
        {
            stream.AppendMany(events);
        }

        var resultVersion = stream.CurrentVersion;
        CommandIdempotency.Stage(
            session,
            actorId,
            metadata,
            fingerprint,
            resultVersion: resultVersion);
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
                await TenantAuthorization.RequireCurrentPermission(
                    store,
                    command.TenantId,
                    actorId,
                    TenantPermissions.CustomersWrite,
                    cancellationToken);
                return new MutationResult(CommandIdempotency.RequireResultVersion(recovered));
            }

            throw;
        }

        return new MutationResult(resultVersion);
    }
}
