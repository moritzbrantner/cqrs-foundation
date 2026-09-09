using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Domain.Users;
using CqrsFoundation.Infrastructure;
using Marten;

namespace CqrsFoundation.Application;

public sealed record CreateTenant(string Name);
public sealed record AddTenantMember(Guid TenantId, Guid UserId, string Role);
public sealed record ChangeTenantMemberRole(Guid TenantId, Guid UserId, string Role);
public sealed record RemoveTenantMember(Guid TenantId, Guid UserId);

public static class CreateTenantHandler
{
    public static async Task<Guid> Handle(
        CreateTenant command,
        Guid actorId,
        IDocumentStore store,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        if (name.Length == 0)
        {
            throw new BusinessRuleException("Tenant name is required.");
        }

        var tenantId = Guid.NewGuid();
        await using var session = store.LightweightSession(SystemTenancy.For(tenantId));
        AuditMetadata.Apply(session, actorId, correlationId);
        session.Events.StartStream<TenantAggregate>(
            tenantId,
            new TenantCreated(tenantId, name, actorId));
        await session.SaveChangesAsync(cancellationToken);
        return tenantId;
    }
}

public static class AddTenantMemberHandler
{
    public static async Task Handle(
        AddTenantMember command,
        Guid actorId,
        IDocumentStore store,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await EnsureUserExists(command.UserId, store, cancellationToken);
        await using var session = store.LightweightSession(SystemTenancy.For(command.TenantId));
        AuditMetadata.Apply(session, actorId, correlationId);

        var stream = await session.Events.FetchForWriting<TenantAggregate>(command.TenantId, cancellationToken);
        var tenant = stream.Aggregate ?? throw new KeyNotFoundException("Tenant not found.");
        EnsureCanManageMembers(tenant, actorId);
        stream.AppendOne(tenant.AddMember(command.UserId, command.Role));
        await session.SaveChangesAsync(cancellationToken);
    }

    internal static async Task EnsureUserExists(Guid userId, IDocumentStore store, CancellationToken cancellationToken)
    {
        await using var query = store.QuerySession(SystemTenancy.Id);
        if (await query.LoadAsync<UserProfile>(userId, cancellationToken) is null)
        {
            throw new KeyNotFoundException("User not found.");
        }
    }

    internal static void EnsureCanManageMembers(TenantAggregate tenant, Guid actorId)
    {
        if (!tenant.Members.TryGetValue(actorId, out var role) ||
            role is not (TenantRoles.Owner or TenantRoles.Admin))
        {
            throw new ForbiddenAccessException("The current user cannot manage tenant members.");
        }
    }
}

public static class ChangeTenantMemberRoleHandler
{
    public static async Task Handle(
        ChangeTenantMemberRole command,
        Guid actorId,
        IDocumentStore store,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession(SystemTenancy.For(command.TenantId));
        AuditMetadata.Apply(session, actorId, correlationId);
        var stream = await session.Events.FetchForWriting<TenantAggregate>(command.TenantId, cancellationToken);
        var tenant = stream.Aggregate ?? throw new KeyNotFoundException("Tenant not found.");
        AddTenantMemberHandler.EnsureCanManageMembers(tenant, actorId);
        stream.AppendOne(tenant.ChangeRole(command.UserId, command.Role));
        await session.SaveChangesAsync(cancellationToken);
    }
}

public static class RemoveTenantMemberHandler
{
    public static async Task Handle(
        RemoveTenantMember command,
        Guid actorId,
        IDocumentStore store,
        string correlationId,
        CancellationToken cancellationToken)
    {
        await using var session = store.LightweightSession(SystemTenancy.For(command.TenantId));
        AuditMetadata.Apply(session, actorId, correlationId);
        var stream = await session.Events.FetchForWriting<TenantAggregate>(command.TenantId, cancellationToken);
        var tenant = stream.Aggregate ?? throw new KeyNotFoundException("Tenant not found.");
        AddTenantMemberHandler.EnsureCanManageMembers(tenant, actorId);
        stream.AppendOne(tenant.RemoveMember(command.UserId));
        await session.SaveChangesAsync(cancellationToken);
    }
}
