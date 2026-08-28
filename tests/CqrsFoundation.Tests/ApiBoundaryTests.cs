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
    public void Read_only_tenant_member_cannot_write()
    {
        var tenant = new TenantContext(Guid.NewGuid(), TenantRoles.Member);

        Assert.ThrowsExactly<ForbiddenAccessException>(() => Endpoints.EnsureCanWrite(tenant));
    }

    [TestMethod]
    public void Tenant_admin_can_write()
    {
        var tenant = new TenantContext(Guid.NewGuid(), TenantRoles.Admin);

        Endpoints.EnsureCanWrite(tenant);
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

    [DataTestMethod]
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
}
