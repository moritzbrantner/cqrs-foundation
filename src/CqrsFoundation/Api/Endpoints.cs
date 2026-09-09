using CqrsFoundation.Application;
using CqrsFoundation.Auth;
using CqrsFoundation.Domain.Common;
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
            httpContext.TraceIdentifier,
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
        var tenantId = await CreateTenantHandler.Handle(
            new CreateTenant(RequireRequestString(request.Name, "name")),
            CurrentUser.Id(httpContext.User),
            store,
            httpContext.TraceIdentifier,
            cancellationToken);
        return Results.Created("/api/tenants/current", new { tenantId });
    }

    private static async Task<IResult> GetCurrentTenant(
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        return Results.Ok(await TenantQueries.GetCurrent(tenant.TenantId, store, cancellationToken));
    }

    private static async Task<IResult> GetTenantMembers(
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        var view = await TenantQueries.GetCurrent(tenant.TenantId, store, cancellationToken);
        return Results.Ok(view.Members);
    }

    private static async Task<IResult> AddTenantMember(
        AddTenantMemberRequest request,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        await AddTenantMemberHandler.Handle(
            new AddTenantMember(
                tenant.TenantId,
                request.UserId,
                RequireRequestString(request.Role, "role")),
            CurrentUser.Id(httpContext.User),
            store,
            httpContext.TraceIdentifier,
            cancellationToken);
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
        await ChangeTenantMemberRoleHandler.Handle(
            new ChangeTenantMemberRole(
                tenant.TenantId,
                userId,
                RequireRequestString(request.Role, "role")),
            CurrentUser.Id(httpContext.User),
            store,
            httpContext.TraceIdentifier,
            cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveTenantMember(
        Guid userId,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        await RemoveTenantMemberHandler.Handle(
            new RemoveTenantMember(tenant.TenantId, userId),
            CurrentUser.Id(httpContext.User),
            store,
            httpContext.TraceIdentifier,
            cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ListCustomers(
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        return Results.Ok(await CustomerQueries.List(tenant.TenantId, store, cancellationToken));
    }

    private static async Task<IResult> CreateCustomer(
        CreateCustomerRequest request,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsureCanWrite(tenant);
        var customerId = await CreateCustomerHandler.Handle(
            new CreateCustomer(
                tenant.TenantId,
                RequireRequestString(request.Name, "name")),
            CurrentUser.Id(httpContext.User),
            store,
            httpContext.TraceIdentifier,
            cancellationToken);
        return Results.Created($"/api/customers/{customerId}", new { customerId });
    }

    private static async Task<IResult> GetCustomer(
        Guid customerId,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        return Results.Ok(await CustomerQueries.Get(tenant.TenantId, customerId, store, cancellationToken));
    }

    private static async Task<IResult> RenameCustomer(
        Guid customerId,
        RenameCustomerRequest request,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsureCanWrite(tenant);
        await RenameCustomerHandler.Handle(
            new RenameCustomer(
                tenant.TenantId,
                customerId,
                RequireRequestString(request.Name, "name")),
            CurrentUser.Id(httpContext.User),
            store,
            httpContext.TraceIdentifier,
            cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DeactivateCustomer(
        Guid customerId,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        EnsureCanWrite(tenant);
        await DeactivateCustomerHandler.Handle(
            new DeactivateCustomer(tenant.TenantId, customerId),
            CurrentUser.Id(httpContext.User),
            store,
            httpContext.TraceIdentifier,
            cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> GetCustomerHistory(
        Guid customerId,
        IDocumentStore store,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var tenant = RequireTenant(httpContext);
        return Results.Ok(await CustomerQueries.History(
            tenant.TenantId,
            customerId,
            store,
            cancellationToken));
    }

    private static TenantContext RequireTenant(HttpContext context) =>
        TenantContext.From(context)
        ?? throw new BadHttpRequestException("X-Tenant-Id header is required for tenant-scoped endpoints.");

    internal static string RequireRequestString(string? value, string fieldName) =>
        value ?? throw new BadHttpRequestException($"The '{fieldName}' field is required.");

    internal static void EnsureCanWrite(TenantContext tenant)
    {
        if (!tenant.CanWrite)
        {
            throw new ForbiddenAccessException("The current user cannot modify tenant resources.");
        }
    }
}
