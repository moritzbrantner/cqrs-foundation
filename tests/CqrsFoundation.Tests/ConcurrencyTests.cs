using CqrsFoundation.Application;
using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Customers;
using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Infrastructure;
using Marten;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CqrsFoundation.Tests;

[TestClass]
public sealed class ConcurrencyTests
{
    [TestMethod]
    public async Task Versioned_customer_query_tracks_the_event_stream_version()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        await SeedTenant(store, tenantId, actorId);

        var customerId = await CreateCustomerHandler.Handle(
            new CreateCustomer(tenantId, "Versioned customer"),
            actorId,
            store,
            new CommandMetadata("versioned-create", "versioned-create-request"),
            CancellationToken.None);

        var first = await CustomerQueries.Get(
            tenantId,
            actorId,
            customerId,
            store,
            CancellationToken.None);
        Assert.AreEqual(1L, first.Version);
        Assert.AreEqual("Versioned customer", first.Value.Name);

        await RenameCustomerHandler.Handle(
            new RenameCustomer(tenantId, customerId, "Versioned customer 2", first.Version),
            actorId,
            store,
            new CommandMetadata("versioned-rename", "versioned-rename-request"),
            CancellationToken.None);

        var second = await CustomerQueries.Get(
            tenantId,
            actorId,
            customerId,
            store,
            CancellationToken.None);
        Assert.AreEqual(2L, second.Version);
        Assert.AreEqual("Versioned customer 2", second.Value.Name);
    }

    [TestMethod]
    public async Task Stale_customer_version_is_rejected_without_appending_an_event()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        await SeedTenant(store, tenantId, actorId);

        var customerId = await CreateCustomerHandler.Handle(
            new CreateCustomer(tenantId, "Stale customer"),
            actorId,
            store,
            new CommandMetadata("stale-create", "stale-create-request"),
            CancellationToken.None);
        await RenameCustomerHandler.Handle(
            new RenameCustomer(tenantId, customerId, "Current name", 1),
            actorId,
            store,
            new CommandMetadata("stale-rename", "stale-rename-request"),
            CancellationToken.None);

        var exception = await Assert.ThrowsExactlyAsync<StaleResourceVersionException>(() =>
            DeactivateCustomerHandler.Handle(
                new DeactivateCustomer(tenantId, customerId, 1),
                actorId,
                store,
                new CommandMetadata("stale-deactivate", "stale-deactivate-request"),
                CancellationToken.None));
        Assert.AreEqual(1L, exception.ExpectedVersion);
        Assert.AreEqual(2L, exception.ActualVersion);

        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        var events = await query.Events.FetchStreamAsync(customerId);
        Assert.HasCount(2, events);
        var customer = await query.LoadAsync<CustomerView>(customerId);
        Assert.IsNotNull(customer);
        Assert.IsTrue(customer.IsActive);
    }

    [TestMethod]
    public async Task Idempotent_retry_replays_before_rechecking_the_old_resource_version()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var key = $"rename-{Guid.NewGuid():N}";
        await SeedTenant(store, tenantId, actorId);

        var customerId = await CreateCustomerHandler.Handle(
            new CreateCustomer(tenantId, "Retry version customer"),
            actorId,
            store,
            new CommandMetadata("retry-version-create", "retry-version-create-request"),
            CancellationToken.None);
        var command = new RenameCustomer(tenantId, customerId, "Renamed once", 1);

        await RenameCustomerHandler.Handle(
            command,
            actorId,
            store,
            new CommandMetadata("retry-version-first", "retry-version-first-request", key),
            CancellationToken.None);
        await RenameCustomerHandler.Handle(
            command,
            actorId,
            store,
            new CommandMetadata("retry-version-replay", "retry-version-replay-request", key),
            CancellationToken.None);

        await using var query = store.QuerySession(SystemTenancy.For(tenantId));
        Assert.HasCount(2, await query.Events.FetchStreamAsync(customerId));
    }

    [TestMethod]
    public async Task Stale_tenant_version_rejects_membership_change()
    {
        var connectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("TEST_POSTGRES is not configured.");
            return;
        }

        using var store = DocumentStore.For(options => Persistence.Configure(options, connectionString));
        var tenantId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await SeedTenant(store, tenantId, ownerId, (adminId, TenantRoles.Admin));

        var exception = await Assert.ThrowsExactlyAsync<StaleResourceVersionException>(() =>
            ChangeTenantMemberRoleHandler.Handle(
                new ChangeTenantMemberRole(tenantId, adminId, TenantRoles.Member, 1),
                ownerId,
                store,
                new CommandMetadata("stale-member", "stale-member-request"),
                CancellationToken.None));
        Assert.AreEqual(1L, exception.ExpectedVersion);
        Assert.AreEqual(2L, exception.ActualVersion);

        var tenant = await TenantQueries.GetCurrent(
            tenantId,
            ownerId,
            store,
            CancellationToken.None);
        Assert.AreEqual(2L, tenant.Version);
        Assert.AreEqual(TenantRoles.Admin, tenant.Value.Members[adminId]);
    }

    private static async Task SeedTenant(
        IDocumentStore store,
        Guid tenantId,
        Guid ownerId,
        params (Guid UserId, string Role)[] members)
    {
        await using var session = store.LightweightSession(SystemTenancy.For(tenantId));
        AuditMetadata.Apply(
            session,
            ownerId,
            new CommandMetadata("concurrency-tenant-seed", $"concurrency-tenant-seed-{tenantId:N}"));
        var events = new List<object>
        {
            new TenantCreated(tenantId, "Concurrency test", ownerId)
        };
        events.AddRange(members.Select(member =>
            (object)new TenantMemberAdded(member.UserId, TenantRoles.Normalize(member.Role))));
        session.Events.StartStream<TenantAggregate>(tenantId, events.ToArray());
        await session.SaveChangesAsync();
    }
}
