using System.Globalization;
using CqrsFoundation.Application;
using CqrsFoundation.Auth;
using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Infrastructure;
using Marten;
using Microsoft.AspNetCore.Identity;

namespace CqrsFoundation.Api;

public sealed record RegisterUserRequest(string? Email, string? Password);
public sealed record LoginUserRequest(string? Email, string? Password);
public sealed record CreateTenantRequest(string? Name);
public sealed record AddTenantMemberRequest(Guid UserId, string? Role);
public sealed record ChangeTenantMemberRoleRequest(string? Role);
public sealed record CreateCustomerRequest(string? Name);
public sealed record RenameCustomerRequest(string? Name);

public static class Endpoints
{
    internal const string CorrelationIdHeader = "X-Correlation-Id";
    internal const string IdempotencyKeyHeader = "Idempotency-Key";
    internal const string IfMatchHeader = "If-Match";
    internal const int MaxIdempotencyKeyLength = 128;

    public static void MapFoundationEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/auth/register", Register);
        api.MapPost("/auth/login", Login);

        var authenticated = api.MapGroup(string.Empty).RequireAuthorization();
        authenticated.MapGet("/users/me", GetCurrentUser);
        authenticated.MapPost("/tenants", CreateTenant);
        authenticated.MapGet("/tenants/current", GetCurrentTenant);
        authenticated.MapGet("/tenants/current/members", GetTenantMembers);
        authenticated.MapPost("/tenants/current/members", AddTenantMember);
        authenticated.MapPut("/tenants/current/members/{userId:guid}/role", ChangeTenantMemberRole);
        authenticated.MapDelete("/tenants/current/members/{userId:guid}", RemoveTenantMember);

        authenticated.MapGet("/customers", ListCustomers);
        authenticated.MapPost("/customers", CreateCustomer);
        authenticated.MapGet("/customers/{customerId:guid}", GetCustomer);
        authenticated.MapPut("/customers/{customerId:guid}/name", RenameCustomer);
        authenticated.MapPost("/customers/{customerId:guid}/deactivate", DeactivateCustomer);
        authenticated.MapGet("/customers/{customerId:guid}/history", GetCustomerHistory);
    }

    private static async Task<IResult> Register(
        RegisterUserRequest request,
        IDocumentStore store,
        IPasswordHasher<Credential> passwordHasher,
        TokenService tokenService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await RegisterUserHandler.Handle(
            new RegisterUser(
                RequireRequestString(request.Email, "email"),
                RequireRequestString(request.Password, "password")),
            store,
            passwordHasher,
            tokenService,
            CommandMetadataFor(httpContext),
            cancellationToken);
        return Results.Created("/api/users/me", result);
    }

    private static async Task<IResult> Login(
        LoginUserRequest request,
        IDocumentStore store,
        IPasswordHasher<Credential> passwordHasher,
        TokenService tokenService,
        CancellationToken cancellationToken)
    {
        var result = await LoginUserHandler.Handle(
            new LoginUser(
                RequireRequestString(request.Email, "email"),
                RequireRequestString(request.Password, "password")),
            store,
            passwordHasher,
            tokenService,
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetCurrentUser(
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        Results.Ok(await UserQueries.GetCurrent(
            CurrentUser.Id(httpContext.User),
            store,
            cancellationToken));

    private static async Task<IResult> CreateTenant(
        CreateTenantRequest request,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await CreateTenantHandler.Handle(
            new CreateTenant(RequireRequestString(request.Name, "name")),
            CurrentUser.Id(httpContext.User),
            store,
            CommandMetadataFor(httpContext),
            cancellationToken);
        SetEntityTag(httpContext, result.Version);
        return Results.Created("/api/tenants/current", new { tenantId = result.ResourceId });
    }

    private static async Task<IResult> GetCurrentTenant(
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsurePermission(tenant, TenantPermissions.TenantRead);
        var result = await TenantQueries.GetCurrent(
            tenant.TenantId,
            CurrentUser.Id(httpContext.User),
            store,
            cancellationToken);
        SetEntityTag(httpContext, result.Version);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetTenantMembers(
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsurePermission(tenant, TenantPermissions.MembersRead);
        var result = await TenantQueries.ListMembers(
            tenant.TenantId,
            CurrentUser.Id(httpContext.User),
            store,
            cancellationToken);
        SetEntityTag(httpContext, result.Version);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> AddTenantMember(
        AddTenantMemberRequest request,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsurePermission(tenant, TenantPermissions.MembersManage);
        var result = await AddTenantMemberHandler.Handle(
            new AddTenantMember(
                tenant.TenantId,
                request.UserId,
                RequireRequestString(request.Role, "role"),
                ExpectedVersionFor(httpContext)),
            CurrentUser.Id(httpContext.User),
            store,
            CommandMetadataFor(httpContext),
            cancellationToken);
        SetEntityTag(httpContext, result.Version);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangeTenantMemberRole(
        Guid userId,
        ChangeTenantMemberRoleRequest request,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsurePermission(tenant, TenantPermissions.MembersManage);
        var result = await ChangeTenantMemberRoleHandler.Handle(
            new ChangeTenantMemberRole(
                tenant.TenantId,
                userId,
                RequireRequestString(request.Role, "role"),
                ExpectedVersionFor(httpContext)),
            CurrentUser.Id(httpContext.User),
            store,
            CommandMetadataFor(httpContext),
            cancellationToken);
        SetEntityTag(httpContext, result.Version);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveTenantMember(
        Guid userId,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsurePermission(tenant, TenantPermissions.MembersManage);
        var result = await RemoveTenantMemberHandler.Handle(
            new RemoveTenantMember(
                tenant.TenantId,
                userId,
                ExpectedVersionFor(httpContext)),
            CurrentUser.Id(httpContext.User),
            store,
            CommandMetadataFor(httpContext),
            cancellationToken);
        SetEntityTag(httpContext, result.Version);
        return Results.NoContent();
    }

    private static async Task<IResult> ListCustomers(
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsurePermission(tenant, TenantPermissions.CustomersRead);
        return Results.Ok(await CustomerQueries.List(
            tenant.TenantId,
            CurrentUser.Id(httpContext.User),
            store,
            cancellationToken));
    }

    private static async Task<IResult> CreateCustomer(
        CreateCustomerRequest request,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsurePermission(tenant, TenantPermissions.CustomersWrite);
        var result = await CreateCustomerHandler.Handle(
            new CreateCustomer(
                tenant.TenantId,
                RequireRequestString(request.Name, "name")),
            CurrentUser.Id(httpContext.User),
            store,
            CommandMetadataFor(httpContext),
            cancellationToken);
        SetEntityTag(httpContext, result.Version);
        return Results.Created(
            $"/api/customers/{result.ResourceId}",
            new { customerId = result.ResourceId });
    }

    private static async Task<IResult> GetCustomer(
        Guid customerId,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsurePermission(tenant, TenantPermissions.CustomersRead);
        var result = await CustomerQueries.Get(
            tenant.TenantId,
            CurrentUser.Id(httpContext.User),
            customerId,
            store,
            cancellationToken);
        SetEntityTag(httpContext, result.Version);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> RenameCustomer(
        Guid customerId,
        RenameCustomerRequest request,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsurePermission(tenant, TenantPermissions.CustomersWrite);
        var result = await RenameCustomerHandler.Handle(
            new RenameCustomer(
                tenant.TenantId,
                customerId,
                RequireRequestString(request.Name, "name"),
                ExpectedVersionFor(httpContext)),
            CurrentUser.Id(httpContext.User),
            store,
            CommandMetadataFor(httpContext),
            cancellationToken);
        SetEntityTag(httpContext, result.Version);
        return Results.NoContent();
    }

    private static async Task<IResult> DeactivateCustomer(
        Guid customerId,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsurePermission(tenant, TenantPermissions.CustomersWrite);
        var result = await DeactivateCustomerHandler.Handle(
            new DeactivateCustomer(
                tenant.TenantId,
                customerId,
                ExpectedVersionFor(httpContext)),
            CurrentUser.Id(httpContext.User),
            store,
            CommandMetadataFor(httpContext),
            cancellationToken);
        SetEntityTag(httpContext, result.Version);
        return Results.NoContent();
    }

    private static async Task<IResult> GetCustomerHistory(
        Guid customerId,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsurePermission(tenant, TenantPermissions.AuditRead);
        return Results.Ok(await CustomerQueries.History(
            tenant.TenantId,
            CurrentUser.Id(httpContext.User),
            customerId,
            store,
            cancellationToken));
    }

    private static TenantContext RequireTenant(HttpContext context) =>
        TenantContext.From(context)
        ?? throw new BadHttpRequestException("X-Tenant-Id header is required for tenant-scoped endpoints.");

    internal static CommandMetadata CommandMetadataFor(HttpContext context)
    {
        var requestedCorrelationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()?.Trim();
        var correlationId = string.IsNullOrWhiteSpace(requestedCorrelationId)
            ? context.TraceIdentifier
            : requestedCorrelationId;

        string? idempotencyKey = null;
        if (context.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var idempotencyValues))
        {
            if (idempotencyValues.Count != 1)
            {
                throw new BadHttpRequestException($"The '{IdempotencyKeyHeader}' header must have exactly one value.");
            }

            idempotencyKey = idempotencyValues[0]?.Trim();
            if (string.IsNullOrWhiteSpace(idempotencyKey))
            {
                throw new BadHttpRequestException($"The '{IdempotencyKeyHeader}' header cannot be empty.");
            }

            if (idempotencyKey.Length > MaxIdempotencyKeyLength)
            {
                throw new BadHttpRequestException(
                    $"The '{IdempotencyKeyHeader}' header cannot exceed {MaxIdempotencyKeyLength} characters.");
            }
        }

        var metadata = new CommandMetadata(correlationId, context.TraceIdentifier, idempotencyKey);
        context.Response.Headers[CorrelationIdHeader] = correlationId;
        return metadata;
    }

    internal static long? ExpectedVersionFor(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(IfMatchHeader, out var values))
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new BadHttpRequestException($"The '{IfMatchHeader}' header must contain exactly one entity tag.");
        }

        var value = values[0]?.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ||
            value.Length < 3 ||
            value[0] != '"' ||
            value[^1] != '"' ||
            !long.TryParse(
                value[1..^1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var version) ||
            version < 1)
        {
            throw new BadHttpRequestException(
                $"The '{IfMatchHeader}' header must be a single strong numeric ETag such as \"3\".");
        }

        return version;
    }

    internal static void SetEntityTag(HttpContext context, long version) =>
        context.Response.Headers.ETag = $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    internal static string RequireRequestString(string? value, string fieldName) =>
        value ?? throw new BadHttpRequestException($"The '{fieldName}' field is required.");

    internal static void EnsurePermission(TenantContext tenant, string permission)
    {
        if (!tenant.HasPermission(permission))
        {
            throw new ForbiddenAccessException(
                $"The current user does not have the '{permission}' permission.");
        }
    }
}
