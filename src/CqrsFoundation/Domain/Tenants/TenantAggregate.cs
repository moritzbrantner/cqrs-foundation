using CqrsFoundation.Domain.Common;

namespace CqrsFoundation.Domain.Tenants;

public static class TenantRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";

    public static string Normalize(string role)
    {
        var normalized = role.Trim().ToLowerInvariant();
        return normalized switch
        {
            Owner or Admin or Member => normalized,
            _ => throw new BusinessRuleException($"Unknown tenant role '{role}'.")
        };
    }
}

public sealed record TenantCreated(Guid TenantId, string Name, Guid OwnerUserId);
public sealed record TenantMemberAdded(Guid UserId, string Role);
public sealed record TenantMemberRoleChanged(Guid UserId, string Role);
public sealed record TenantMemberRemoved(Guid UserId);

public sealed record TenantAggregate(
    Guid Id,
    string Name,
    Guid OwnerUserId,
    Dictionary<Guid, string> Members)
{
    public static TenantAggregate Create(TenantCreated @event) =>
        new(
            @event.TenantId,
            @event.Name,
            @event.OwnerUserId,
            new Dictionary<Guid, string> { [@event.OwnerUserId] = TenantRoles.Owner });

    public static TenantAggregate Apply(TenantMemberAdded @event, TenantAggregate tenant)
    {
        var members = new Dictionary<Guid, string>(tenant.Members)
        {
            [@event.UserId] = @event.Role
        };
        return tenant with { Members = members };
    }

    public static TenantAggregate Apply(TenantMemberRoleChanged @event, TenantAggregate tenant)
    {
        var members = new Dictionary<Guid, string>(tenant.Members)
        {
            [@event.UserId] = @event.Role
        };
        return tenant with { Members = members };
    }

    public static TenantAggregate Apply(TenantMemberRemoved @event, TenantAggregate tenant)
    {
        var members = new Dictionary<Guid, string>(tenant.Members);
        members.Remove(@event.UserId);
        return tenant with { Members = members };
    }

    public object AddMember(Guid userId, string role)
    {
        if (Members.ContainsKey(userId))
        {
            throw new BusinessRuleException("The user is already a tenant member.");
        }

        return new TenantMemberAdded(userId, TenantRoles.Normalize(role));
    }

    public object ChangeRole(Guid userId, string role)
    {
        if (userId == OwnerUserId)
        {
            throw new BusinessRuleException("The tenant owner role cannot be changed.");
        }

        if (!Members.ContainsKey(userId))
        {
            throw new BusinessRuleException("The user is not a tenant member.");
        }

        return new TenantMemberRoleChanged(userId, TenantRoles.Normalize(role));
    }

    public object RemoveMember(Guid userId)
    {
        if (userId == OwnerUserId)
        {
            throw new BusinessRuleException("The tenant owner cannot be removed.");
        }

        if (!Members.ContainsKey(userId))
        {
            throw new BusinessRuleException("The user is not a tenant member.");
        }

        return new TenantMemberRemoved(userId);
    }
}

public sealed record TenantView(
    Guid Id,
    string Name,
    Guid OwnerUserId,
    Dictionary<Guid, string> Members)
{
    public static TenantView Create(TenantCreated @event) =>
        new(
            @event.TenantId,
            @event.Name,
            @event.OwnerUserId,
            new Dictionary<Guid, string> { [@event.OwnerUserId] = TenantRoles.Owner });

    public static TenantView Apply(TenantMemberAdded @event, TenantView tenant)
    {
        var members = new Dictionary<Guid, string>(tenant.Members)
        {
            [@event.UserId] = @event.Role
        };
        return tenant with { Members = members };
    }

    public static TenantView Apply(TenantMemberRoleChanged @event, TenantView tenant)
    {
        var members = new Dictionary<Guid, string>(tenant.Members)
        {
            [@event.UserId] = @event.Role
        };
        return tenant with { Members = members };
    }

    public static TenantView Apply(TenantMemberRemoved @event, TenantView tenant)
    {
        var members = new Dictionary<Guid, string>(tenant.Members);
        members.Remove(@event.UserId);
        return tenant with { Members = members };
    }
}
