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
    private const string Operation = "tenants.create.v1";

    public static async Task<Guid> Handle(
        CreateTenant command,
        Guid actorId,
        IDocumentStore store,
        CommandMetadata metadata,
        CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        if (name.Length == 0)
        {
            throw new BusinessRuleException("Tenant name is required.");
        }

        var tenantId = CommandIdempotency.NewResourceId(actorId, Operation, metadata);
        var tenancyId = SystemTenancy.For(tenantId);
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

        AuditMetadata.Apply(session, actorId, metadata);
        session.Events.StartStream<TenantAggregate>(
            tenantId,
            new TenantCreated(tenantId, name, actorId));
        CommandIdempotency.Stage(session, actorId, metadata, fingerprint, tenantId);

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

        return tenantId;
    }
}

public static class AddTenantMemberHandler
{
    private const string Operation = "tenants.members.add.v1";

    public static async Task Handle(
        AddTenantMember command,
        Guid actorId,
        IDocumentStore store,
        CommandMetadata metadata,
        CancellationToken cancellationToken)
    {
        var role = TenantRoles.Normalize(command.Role);
        var tenancyId = SystemTenancy.For(command.TenantId);
        var fingerprint = CommandIdempotency.Fingerprint(
            Operation,
            command.UserId.ToString("D"),
            role);
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
        var stream = await session.Events.FetchForWriting<TenantAggregate>(command.TenantId, cancellationToken);
        var tenant = stream.Aggregate ?? throw new KeyNotFoundException("Tenant not found.");
        EnsureCanManageMembers(tenant, actorId);
        await EnsureUserExists(command.UserId, store, cancellationToken);
        stream.AppendOne(tenant.AddMember(command.UserId, role));
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
    private const string Operation = "tenants.members.change-role.v1";

    public static async Task Handle(
        ChangeTenantMemberRole command,
        Guid actorId,
        IDocumentStore store,
        CommandMetadata metadata,
        CancellationToken cancellationToken)
    {
        var role = TenantRoles.Normalize(command.Role);
        var tenancyId = SystemTenancy.For(command.TenantId);
        var fingerprint = CommandIdempotency.Fingerprint(
            Operation,
            command.UserId.ToString("D"),
            role);
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
        var stream = await session.Events.FetchForWriting<TenantAggregate>(command.TenantId, cancellationToken);
        var tenant = stream.Aggregate ?? throw new KeyNotFoundException("Tenant not found.");
        AddTenantMemberHandler.EnsureCanManageMembers(tenant, actorId);
        stream.AppendOne(tenant.ChangeRole(command.UserId, role));
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

public static class RemoveTenantMemberHandler
{
    private const string Operation = "tenants.members.remove.v1";

    public static async Task Handle(
        RemoveTenantMember command,
        Guid actorId,
        IDocumentStore store,
        CommandMetadata metadata,
        CancellationToken cancellationToken)
    {
        var tenancyId = SystemTenancy.For(command.TenantId);
        var fingerprint = CommandIdempotency.Fingerprint(
            Operation,
            command.UserId.ToString("D"));
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
        var stream = await session.Events.FetchForWriting<TenantAggregate>(command.TenantId, cancellationToken);
        var tenant = stream.Aggregate ?? throw new KeyNotFoundException("Tenant not found.");
        AddTenantMemberHandler.EnsureCanManageMembers(tenant, actorId);
        stream.AppendOne(tenant.RemoveMember(command.UserId));
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
