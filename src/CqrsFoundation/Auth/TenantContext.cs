using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Infrastructure;
using Marten;

namespace CqrsFoundation.Auth;

public sealed record TenantContext(Guid TenantId, string Role)
{
    public const string ItemKey = "cqrs-foundation.tenant";

    public bool CanWrite => Role is TenantRoles.Owner or TenantRoles.Admin;
    public bool CanManageMembers => Role is TenantRoles.Owner or TenantRoles.Admin;

    public static TenantContext? From(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) ? value as TenantContext : null;
}

public sealed class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IDocumentStore store)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            !context.Request.Headers.TryGetValue("X-Tenant-Id", out var rawTenantId))
        {
            await next(context);
            return;
        }

        if (!Guid.TryParse(rawTenantId.ToString(), out var tenantId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "X-Tenant-Id must be a GUID." });
            return;
        }

        var userId = CurrentUser.Id(context.User);
        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        var tenant = await query.LoadAsync<TenantView>(tenantId, context.RequestAborted);

        if (tenant is null || !tenant.Members.TryGetValue(userId, out var role))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "The user is not a member of this tenant." });
            return;
        }

        context.Items[TenantContext.ItemKey] = new TenantContext(tenantId, role);
        await next(context);
    }
}
