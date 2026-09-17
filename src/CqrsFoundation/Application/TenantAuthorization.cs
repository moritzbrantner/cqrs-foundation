using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Infrastructure;
using Marten;

namespace CqrsFoundation.Application;

public static class TenantAuthorization
{
    public static async Task<TenantAggregate> RequireCommandPermission(
        IDocumentSession session,
        Guid tenantId,
        Guid actorId,
        string permission,
        CancellationToken cancellationToken)
    {
        var stream = await session.Events.FetchForWriting<TenantAggregate>(tenantId, cancellationToken);
        var tenant = stream.Aggregate;
        if (tenant is null)
        {
            throw new ForbiddenAccessException("The current user cannot access this tenant.");
        }

        EnsurePermission(tenant.Members, actorId, permission);
        stream.AlwaysEnforceConsistency = true;
        return tenant;
    }

    public static async Task<TenantView> RequireQueryPermission(
        IQuerySession query,
        Guid tenantId,
        Guid actorId,
        string permission,
        CancellationToken cancellationToken)
    {
        var tenant = await query.LoadAsync<TenantView>(tenantId, cancellationToken);
        if (tenant is null)
        {
            throw new ForbiddenAccessException("The current user cannot access this tenant.");
        }

        EnsurePermission(tenant.Members, actorId, permission);
        return tenant;
    }

    public static async Task RequireCurrentPermission(
        IDocumentStore store,
        Guid tenantId,
        Guid actorId,
        string permission,
        CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        await RequireQueryPermission(
            query,
            tenantId,
            actorId,
            permission,
            cancellationToken);
    }

    public static void EnsurePermission(
        IReadOnlyDictionary<Guid, string> members,
        Guid actorId,
        string permission)
    {
        if (!members.TryGetValue(actorId, out var role) || !TenantRoles.Grants(role, permission))
        {
            throw new ForbiddenAccessException(
                $"The current user does not have the '{permission}' permission.");
        }
    }
}
