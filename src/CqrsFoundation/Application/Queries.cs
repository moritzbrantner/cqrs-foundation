using CqrsFoundation.Domain.Customers;
using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Domain.Users;
using CqrsFoundation.Infrastructure;
using Marten;

namespace CqrsFoundation.Application;

public sealed record VersionedResource<T>(T Value, long Version);

public sealed record EventHistoryItem(
    Guid EventId,
    long Version,
    DateTimeOffset Timestamp,
    string Type,
    string? ActorId,
    string? CorrelationId,
    string? CausationId,
    object Data);

public static class UserQueries
{
    public static async Task<UserProfile> GetCurrent(
        Guid userId,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.Id);
        return await query.LoadAsync<UserProfile>(userId, cancellationToken)
            ?? throw new KeyNotFoundException("User not found.");
    }
}

public static class TenantQueries
{
    public static async Task<VersionedResource<TenantView>> GetCurrent(
        Guid tenantId,
        Guid actorId,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        var tenant = await TenantAuthorization.RequireQueryPermission(
            query,
            tenantId,
            actorId,
            TenantPermissions.TenantRead,
            cancellationToken);
        var state = await query.Events.FetchStreamStateAsync(tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Tenant not found.");
        return new VersionedResource<TenantView>(tenant, state.Version);
    }

    public static async Task<VersionedResource<IReadOnlyDictionary<Guid, string>>> ListMembers(
        Guid tenantId,
        Guid actorId,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        var tenant = await TenantAuthorization.RequireQueryPermission(
            query,
            tenantId,
            actorId,
            TenantPermissions.MembersRead,
            cancellationToken);
        var state = await query.Events.FetchStreamStateAsync(tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Tenant not found.");
        return new VersionedResource<IReadOnlyDictionary<Guid, string>>(tenant.Members, state.Version);
    }
}

public static class CustomerQueries
{
    public static async Task<IReadOnlyList<CustomerView>> List(
        Guid tenantId,
        Guid actorId,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        await TenantAuthorization.RequireQueryPermission(
            query,
            tenantId,
            actorId,
            TenantPermissions.CustomersRead,
            cancellationToken);
        return await query.Query<CustomerView>()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public static async Task<VersionedResource<CustomerView>> Get(
        Guid tenantId,
        Guid actorId,
        Guid customerId,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        await TenantAuthorization.RequireQueryPermission(
            query,
            tenantId,
            actorId,
            TenantPermissions.CustomersRead,
            cancellationToken);
        var customer = await query.LoadAsync<CustomerView>(customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Customer not found.");
        var state = await query.Events.FetchStreamStateAsync(customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Customer not found.");
        return new VersionedResource<CustomerView>(customer, state.Version);
    }

    public static async Task<IReadOnlyList<EventHistoryItem>> History(
        Guid tenantId,
        Guid actorId,
        Guid customerId,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        await TenantAuthorization.RequireQueryPermission(
            query,
            tenantId,
            actorId,
            TenantPermissions.AuditRead,
            cancellationToken);
        var events = await query.Events.FetchStreamAsync(customerId, token: cancellationToken);
        if (events.Count == 0)
        {
            throw new KeyNotFoundException("Customer not found.");
        }

        return events.Select(@event =>
        {
            string? actorIdHeader = null;
            if (@event.Headers is not null && @event.Headers.TryGetValue("actor_id", out var actor))
            {
                actorIdHeader = actor?.ToString();
            }

            return new EventHistoryItem(
                @event.Id,
                @event.Version,
                @event.Timestamp,
                @event.Data.GetType().Name,
                actorIdHeader,
                @event.CorrelationId,
                @event.CausationId,
                @event.Data);
        }).ToArray();
    }
}
