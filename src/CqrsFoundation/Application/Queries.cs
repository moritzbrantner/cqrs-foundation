using CqrsFoundation.Domain.Customers;
using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Domain.Users;
using CqrsFoundation.Infrastructure;
using Marten;

namespace CqrsFoundation.Application;

public sealed record EventHistoryItem(
    Guid EventId,
    long Version,
    DateTimeOffset Timestamp,
    string Type,
    string? ActorId,
    string? CorrelationId,
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
    public static async Task<TenantView> GetCurrent(
        Guid tenantId,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        return await query.LoadAsync<TenantView>(tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Tenant not found.");
    }
}

public static class CustomerQueries
{
    public static async Task<IReadOnlyList<CustomerView>> List(
        Guid tenantId,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        return await query.Query<CustomerView>()
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public static async Task<CustomerView> Get(
        Guid tenantId,
        Guid customerId,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        return await query.LoadAsync<CustomerView>(customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Customer not found.");
    }

    public static async Task<IReadOnlyList<EventHistoryItem>> History(
        Guid tenantId,
        Guid customerId,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        var events = await query.Events.FetchStreamAsync(customerId, token: cancellationToken);
        if (events.Count == 0)
        {
            throw new KeyNotFoundException("Customer not found.");
        }

        return events.Select(@event =>
        {
            string? actorId = null;
            if (@event.Headers is not null && @event.Headers.TryGetValue("actor_id", out var actor))
            {
                actorId = actor?.ToString();
            }

            return new EventHistoryItem(
                @event.Id,
                @event.Version,
                @event.Timestamp,
                @event.Data.GetType().Name,
                actorId,
                @event.CorrelationId,
                @event.Data);
        }).ToArray();
    }
}
