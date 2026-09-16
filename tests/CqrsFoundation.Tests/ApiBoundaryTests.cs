using System.Text.Json;
using CqrsFoundation.Api;
using CqrsFoundation.Auth;
using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Tenants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CqrsFoundation.Tests;

[TestClass]
public sealed class ApiBoundaryTests
{
    [TestMethod]
    public void Read_only_tenant_member_cannot_write_customers()
    {
        var tenant = new TenantContext(Guid.NewGuid(), TenantRoles.Member);

        Assert.ThrowsExactly<ForbiddenAccessException>(
            () => Endpoints.EnsurePermission(tenant, TenantPermissions.CustomersWrite));
    }

    [TestMethod]
    public void Read_only_tenant_member_can_read_customers()
    {
        var tenant = new TenantContext(Guid.NewGuid(), TenantRoles.Member);

        Endpoints.EnsurePermission(tenant, TenantPermissions.CustomersRead);
    }

    [TestMethod]
    public void Tenant_admin_can_write_and_manage_members()
    {
        var tenant = new TenantContext(Guid.NewGuid(), TenantRoles.Admin);

        Endpoints.EnsurePermission(tenant, TenantPermissions.CustomersWrite);
        Endpoints.EnsurePermission(tenant, TenantPermissions.MembersManage);
    }

    [TestMethod]
    public async Task Authenticated_permission_failure_maps_to_forbidden()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await new ApiExceptionHandler().TryHandleAsync(
            context,
            new ForbiddenAccessException("Denied."),
            CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(context.Response.Body);
        Assert.AreEqual("Forbidden", problem?.Title);
    }

    [TestMethod]
    [DataRow("email")]
    [DataRow("password")]
    [DataRow("name")]
    [DataRow("role")]
    public void Null_request_strings_are_rejected_as_bad_requests(string fieldName)
    {
        var exception = Assert.ThrowsExactly<BadHttpRequestException>(
            () => Endpoints.RequireRequestString(null, fieldName));

        StringAssert.Contains(exception.Message, fieldName);
    }

    [TestMethod]
    public void Command_metadata_preserves_caller_correlation_and_uses_request_as_causation()
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "request-123"
        };
        context.Request.Headers[Endpoints.CorrelationIdHeader] = " business-flow-42 ";
        context.Request.Headers[Endpoints.IdempotencyKeyHeader] = " create-customer-17 ";

        var metadata = Endpoints.CommandMetadataFor(context);

        Assert.AreEqual("business-flow-42", metadata.CorrelationId);
        Assert.AreEqual("request-123", metadata.CausationId);
        Assert.AreEqual("create-customer-17", metadata.IdempotencyKey);
        Assert.AreEqual("business-flow-42", context.Response.Headers[Endpoints.CorrelationIdHeader].ToString());
    }

    [TestMethod]
    public void Command_metadata_falls_back_to_request_trace_for_new_correlation()
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "request-456"
        };

        var metadata = Endpoints.CommandMetadataFor(context);

        Assert.AreEqual("request-456", metadata.CorrelationId);
        Assert.AreEqual("request-456", metadata.CausationId);
        Assert.IsNull(metadata.IdempotencyKey);
        Assert.AreEqual("request-456", context.Response.Headers[Endpoints.CorrelationIdHeader].ToString());
    }

    [TestMethod]
    public void Empty_idempotency_key_is_rejected()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[Endpoints.IdempotencyKeyHeader] = "   ";

        Assert.ThrowsExactly<BadHttpRequestException>(() => Endpoints.CommandMetadataFor(context));
    }

    [TestMethod]
    public void Oversized_idempotency_key_is_rejected()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[Endpoints.IdempotencyKeyHeader] = new string('x', Endpoints.MaxIdempotencyKeyLength + 1);

        Assert.ThrowsExactly<BadHttpRequestException>(() => Endpoints.CommandMetadataFor(context));
    }
}
