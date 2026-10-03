using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Customers;
using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Domain.Users;
using CqrsFoundation.Infrastructure;
using Marten;

namespace CqrsFoundation.Application;

public sealed record VersionedResource<T>(T Value, long Version);

public sealed record CustomerListQuery(
    string? NamePrefix = null,
    bool? IsActive = null,
    string? Cursor = null,
    int Limit = 25)
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    public CustomerListQuery ValidateAndNormalize()
    {
        if (Limit is < 1 or > MaxLimit)
        {
            throw new InvalidQueryException(
                $"Customer query limit must be between 1 and {MaxLimit}.");
        }

        var prefix = NamePrefix?.Trim();
        var cursor = Cursor?.Trim();
        return this with
        {
            NamePrefix = string.IsNullOrEmpty(prefix) ? null : prefix,
            Cursor = string.IsNullOrEmpty(cursor) ? null : cursor
        };
    }
}

public sealed record CustomerQueryPage(
    IReadOnlyList<CustomerView> Items,
    string? NextCursor);

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
        await TenantAuthorization.RequireQueryPermission(
            query,
            tenantId,
            actorId,
            TenantPermissions.TenantRead,
            cancellationToken);
        var state = await query.Events.FetchStreamStateAsync(tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Tenant not found.");
        var tenant = await TenantAuthorization.RequireQueryPermission(
            query,
            tenantId,
            actorId,
            TenantPermissions.TenantRead,
            cancellationToken);
        return new VersionedResource<TenantView>(tenant, state.Version);
    }

    public static async Task<VersionedResource<IReadOnlyDictionary<Guid, string>>> ListMembers(
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
            TenantPermissions.MembersRead,
            cancellationToken);
        var state = await query.Events.FetchStreamStateAsync(tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Tenant not found.");
        var tenant = await TenantAuthorization.RequireQueryPermission(
            query,
            tenantId,
            actorId,
            TenantPermissions.MembersRead,
            cancellationToken);
        return new VersionedResource<IReadOnlyDictionary<Guid, string>>(tenant.Members, state.Version);
    }
}

public static class CustomerQueries
{
    public static Task<CustomerQueryPage> List(
        Guid tenantId,
        Guid actorId,
        IDocumentStore store,
        CancellationToken cancellationToken) =>
        List(
            tenantId,
            actorId,
            new CustomerListQuery(),
            store,
            cancellationToken);

    public static async Task<CustomerQueryPage> List(
        Guid tenantId,
        Guid actorId,
        CustomerListQuery request,
        IDocumentStore store,
        CancellationToken cancellationToken)
    {
        var normalized = request.ValidateAndNormalize();

        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        await TenantAuthorization.RequireQueryPermission(
            query,
            tenantId,
            actorId,
            TenantPermissions.CustomersRead,
            cancellationToken);

        IQueryable<CustomerView> customers = query.Query<CustomerView>();
        if (normalized.NamePrefix is not null)
        {
            customers = customers.Where(x => x.Name.StartsWith(normalized.NamePrefix));
        }

        if (normalized.IsActive is not null)
        {
            customers = customers.Where(x => x.IsActive == normalized.IsActive.Value);
        }

        if (normalized.Cursor is not null)
        {
            var cursor = CustomerQueryCursor.Decode(normalized.Cursor, tenantId, normalized);
            var cursorName = cursor.Name;
            var cursorId = cursor.Id;
            customers = customers.Where(x =>
                x.Name.CompareTo(cursorName) > 0 ||
                (x.Name == cursorName && x.Id.CompareTo(cursorId) > 0));
        }

        var rows = await customers
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Take(normalized.Limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > normalized.Limit;
        var items = rows.Take(normalized.Limit).ToArray();
        var nextCursor = hasMore
            ? CustomerQueryCursor.Encode(tenantId, normalized, items[^1])
            : null;
        return new CustomerQueryPage(items, nextCursor);
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
        var state = await query.Events.FetchStreamStateAsync(customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Customer not found.");
        var customer = await query.LoadAsync<CustomerView>(customerId, cancellationToken)
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
