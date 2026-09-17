using CqrsFoundation.Application;
using CqrsFoundation.Domain.Common;
using CqrsFoundation.Domain.Customers;
using CqrsFoundation.Domain.Tenants;
using CqrsFoundation.Infrastructure;
using Marten;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CqrsFoundation.Tests;

[TestClass]
public sealed class QueryTests
{
    [TestMethod]
    public void Customer_query_normalizes_inputs_and_rejects_unbounded_shapes()
    {
        var normalized = new CustomerListQuery("  Al ", true, "  cursor-token  ", 10)
            .ValidateAndNormalize();

        Assert.AreEqual("Al", normalized.NamePrefix);
        Assert.AreEqual("cursor-token", normalized.Cursor);
        Assert.AreEqual(10, normalized.Limit);
        Assert.IsNull(new CustomerListQuery(Cursor: "   ").ValidateAndNormalize().Cursor);
        Assert.ThrowsExactly<InvalidQueryException>(
            () => new CustomerListQuery(Limit: 0).ValidateAndNormalize());
        Assert.ThrowsExactly<InvalidQueryException>(
            () => new CustomerListQuery(Limit: CustomerListQuery.MaxLimit + 1).ValidateAndNormalize());
    }

    [TestMethod]
    public void Customer_cursor_is_bound_to_tenant_and_filter_shape()
    {
        var tenantId = Guid.NewGuid();
        var query = new CustomerListQuery(" Al ", true, Limit: 10).ValidateAndNormalize();
        var cursor = CustomerQueryCursor.Encode(
            tenantId,
            query,
            new CustomerView(Guid.NewGuid(), "Alpha", true));

        var decoded = CustomerQueryCursor.Decode(cursor, tenantId, query);
        Assert.AreEqual("Alpha", decoded.Name);

        Assert.ThrowsExactly<InvalidQueryException>(() =>
            CustomerQueryCursor.Decode(
                cursor,
                Guid.NewGuid(),
                query));
        Assert.ThrowsExactly<InvalidQueryException>(() =>
            CustomerQueryCursor.Decode(
                cursor,
                tenantId,
                query with { IsActive = false }));
        Assert.ThrowsExactly<InvalidQueryException>(() =>
            CustomerQueryCursor.Decode("not-a-cursor", tenantId, query));
    }

    [TestMethod]
    public async Task Customer_query_filters_and_pages_in_stable_name_then_id_order()
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
        var alpha1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var alpha2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var beta = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var alpine = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var gamma = Guid.Parse("00000000-0000-0000-0000-000000000005");

        await SeedTenant(store, tenantId, actorId);
        await SeedCustomer(store, tenantId, alpha2, "Alpha", true);
        await SeedCustomer(store, tenantId, gamma, "Gamma", false);
        await SeedCustomer(store, tenantId, alpha1, "Alpha", true);
        await SeedCustomer(store, tenantId, beta, "Beta", true);
        await SeedCustomer(store, tenantId, alpine, "Alpine", false);

        var first = await CustomerQueries.List(
            tenantId,
            actorId,
            new CustomerListQuery(" Al ", true, Limit: 1),
            store,
            CancellationToken.None);

        Assert.HasCount(1, first.Items);
        Assert.AreEqual(alpha1, first.Items[0].Id);
        Assert.IsNotNull(first.NextCursor);

        var second = await CustomerQueries.List(
            tenantId,
            actorId,
            new CustomerListQuery("Al", true, first.NextCursor, 1),
            store,
            CancellationToken.None);

        Assert.HasCount(1, second.Items);
        Assert.AreEqual(alpha2, second.Items[0].Id);
        Assert.IsNull(second.NextCursor);

        var inactive = await CustomerQueries.List(
            tenantId,
            actorId,
            new CustomerListQuery(IsActive: false, Limit: 10),
            store,
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { alpine, gamma },
            inactive.Items.Select(x => x.Id).ToArray());
        Assert.IsNull(inactive.NextCursor);
    }

    [TestMethod]
    public async Task Keyset_cursor_does_not_shift_when_a_row_is_inserted_before_it()
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
        var beta = Guid.NewGuid();
        var charlie = Guid.NewGuid();
        var delta = Guid.NewGuid();
        await SeedTenant(store, tenantId, actorId);
        await SeedCustomer(store, tenantId, beta, "Beta", true);
        await SeedCustomer(store, tenantId, charlie, "Charlie", true);
        await SeedCustomer(store, tenantId, delta, "Delta", true);

        var first = await CustomerQueries.List(
            tenantId,
            actorId,
            new CustomerListQuery(Limit: 2),
            store,
            CancellationToken.None);
        CollectionAssert.AreEqual(
            new[] { beta, charlie },
            first.Items.Select(x => x.Id).ToArray());
        Assert.IsNotNull(first.NextCursor);

        await SeedCustomer(store, tenantId, Guid.NewGuid(), "Alpha", true);

        var second = await CustomerQueries.List(
            tenantId,
            actorId,
            new CustomerListQuery(Cursor: first.NextCursor, Limit: 2),
            store,
            CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { delta },
            second.Items.Select(x => x.Id).ToArray());
        Assert.IsNull(second.NextCursor);
    }

    [TestMethod]
    public async Task Default_customer_query_is_bounded()
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

        for (var i = 0; i < CustomerListQuery.DefaultLimit + 1; i++)
        {
            await SeedCustomer(
                store,
                tenantId,
                Guid.NewGuid(),
                $"Customer {i:D3}",
                true);
        }

        var page = await CustomerQueries.List(
            tenantId,
            actorId,
            store,
            CancellationToken.None);

        Assert.HasCount(CustomerListQuery.DefaultLimit, page.Items);
        Assert.IsNotNull(page.NextCursor);
    }

    private static async Task SeedTenant(
        IDocumentStore store,
        Guid tenantId,
        Guid ownerId)
    {
        await using var session = store.LightweightSession(SystemTenancy.For(tenantId));
        AuditMetadata.Apply(
            session,
            ownerId,
            new CommandMetadata("query-tenant-seed", $"query-tenant-seed-{tenantId:N}"));
        session.Events.StartStream<TenantAggregate>(
            tenantId,
            new TenantCreated(tenantId, "Query test", ownerId));
        await session.SaveChangesAsync();
    }

    private static async Task SeedCustomer(
        IDocumentStore store,
        Guid tenantId,
        Guid customerId,
        string name,
        bool isActive)
    {
        await using var session = store.LightweightSession(SystemTenancy.For(tenantId));
        if (isActive)
        {
            session.Events.StartStream<CustomerAggregate>(
                customerId,
                new CustomerCreated(customerId, name));
        }
        else
        {
            session.Events.StartStream<CustomerAggregate>(
                customerId,
                new CustomerCreated(customerId, name),
                new CustomerDeactivated());
        }

        await session.SaveChangesAsync();
    }
}
